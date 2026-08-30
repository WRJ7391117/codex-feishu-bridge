#include <CoreFoundation/CoreFoundation.h>
#include <IOKit/hid/IOHIDKeys.h>
#include <IOKit/hid/IOHIDManager.h>

#include <stdbool.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#define PROMLIGHT_VENDOR_ID 2007
#define PROMLIGHT_PRODUCT_ID 27393
#define PROMLIGHT_USAGE_PAGE 0xFF00
#define PROMLIGHT_USAGE 0x01
#define PROMLIGHT_RELEASE_013 19
#define HELPER_VERSION "1"
#define REPORT_SIZE 64
#define REPORT_ID 2

typedef struct {
    IOHIDDeviceRef device;
    char relay_ref[256];
    char product[128];
    long release_number;
} PromLightDevice;

static void json_string(const char *value) {
    putchar('"');
    for (const unsigned char *cursor = (const unsigned char *)value; *cursor; cursor++) {
        switch (*cursor) {
            case '"': fputs("\\\"", stdout); break;
            case '\\': fputs("\\\\", stdout); break;
            case '\b': fputs("\\b", stdout); break;
            case '\f': fputs("\\f", stdout); break;
            case '\n': fputs("\\n", stdout); break;
            case '\r': fputs("\\r", stdout); break;
            case '\t': fputs("\\t", stdout); break;
            default:
                if (*cursor < 0x20) {
                    printf("\\u%04x", *cursor);
                } else {
                    putchar(*cursor);
                }
        }
    }
    putchar('"');
}

static bool copy_string_property(
    IOHIDDeviceRef device,
    CFStringRef key,
    char *destination,
    size_t capacity
) {
    if (capacity == 0) return false;
    destination[0] = '\0';
    CFTypeRef value = IOHIDDeviceGetProperty(device, key);
    if (!value || CFGetTypeID(value) != CFStringGetTypeID()) return false;
    return CFStringGetCString((CFStringRef)value, destination, capacity, kCFStringEncodingUTF8);
}

static long number_property(IOHIDDeviceRef device, CFStringRef key) {
    CFTypeRef value = IOHIDDeviceGetProperty(device, key);
    long result = 0;
    if (value && CFGetTypeID(value) == CFNumberGetTypeID()) {
        CFNumberGetValue((CFNumberRef)value, kCFNumberLongType, &result);
    }
    return result;
}

static void dictionary_set_number(CFMutableDictionaryRef dictionary, CFStringRef key, int value) {
    CFNumberRef number = CFNumberCreate(kCFAllocatorDefault, kCFNumberIntType, &value);
    if (!number) return;
    CFDictionarySetValue(dictionary, key, number);
    CFRelease(number);
}

static IOHIDManagerRef create_manager(void) {
    IOHIDManagerRef manager = IOHIDManagerCreate(kCFAllocatorDefault, kIOHIDOptionsTypeNone);
    if (!manager) return NULL;
    CFMutableDictionaryRef matching = CFDictionaryCreateMutable(
        kCFAllocatorDefault,
        0,
        &kCFTypeDictionaryKeyCallBacks,
        &kCFTypeDictionaryValueCallBacks
    );
    if (!matching) {
        CFRelease(manager);
        return NULL;
    }
    dictionary_set_number(matching, CFSTR(kIOHIDVendorIDKey), PROMLIGHT_VENDOR_ID);
    dictionary_set_number(matching, CFSTR(kIOHIDProductIDKey), PROMLIGHT_PRODUCT_ID);
    dictionary_set_number(matching, CFSTR(kIOHIDPrimaryUsagePageKey), PROMLIGHT_USAGE_PAGE);
    dictionary_set_number(matching, CFSTR(kIOHIDPrimaryUsageKey), PROMLIGHT_USAGE);
    IOHIDManagerSetDeviceMatching(manager, matching);
    CFRelease(matching);
    if (IOHIDManagerOpen(manager, kIOHIDOptionsTypeNone) != kIOReturnSuccess) {
        CFRelease(manager);
        return NULL;
    }
    return manager;
}

static size_t copy_devices(IOHIDManagerRef manager, PromLightDevice **result) {
    *result = NULL;
    CFSetRef device_set = IOHIDManagerCopyDevices(manager);
    if (!device_set) return 0;
    CFIndex count = CFSetGetCount(device_set);
    if (count <= 0) {
        CFRelease(device_set);
        return 0;
    }
    IOHIDDeviceRef *references = calloc((size_t)count, sizeof(*references));
    PromLightDevice *devices = calloc((size_t)count, sizeof(*devices));
    if (!references || !devices) {
        free(references);
        free(devices);
        CFRelease(device_set);
        return 0;
    }
    CFSetGetValues(device_set, (const void **)references);
    size_t accepted = 0;
    for (CFIndex index = 0; index < count; index++) {
        PromLightDevice candidate = {.device = references[index]};
        if (!copy_string_property(
                candidate.device,
                CFSTR(kIOHIDSerialNumberKey),
                candidate.relay_ref,
                sizeof(candidate.relay_ref)
            ) || candidate.relay_ref[0] == '\0') {
            continue;
        }
        if (!copy_string_property(
                candidate.device,
                CFSTR(kIOHIDProductKey),
                candidate.product,
                sizeof(candidate.product)
            ) || candidate.product[0] == '\0') {
            strlcpy(candidate.product, "PromLight", sizeof(candidate.product));
        }
        candidate.release_number = number_property(
            candidate.device,
            CFSTR(kIOHIDVersionNumberKey)
        );
        devices[accepted++] = candidate;
    }
    free(references);
    CFRelease(device_set);
    *result = devices;
    return accepted;
}

static const char *device_version(long release_number) {
    return release_number == PROMLIGHT_RELEASE_013 ? "0.1.3" : "";
}

static int list_devices(void) {
    IOHIDManagerRef manager = create_manager();
    if (!manager) {
        puts("{\"ok\":false,\"status\":\"manager-unavailable\",\"devices\":[]}");
        return 2;
    }
    PromLightDevice *devices = NULL;
    size_t count = copy_devices(manager, &devices);
    fputs("{\"ok\":true,\"helper_version\":\"" HELPER_VERSION "\",\"devices\":[", stdout);
    for (size_t index = 0; index < count; index++) {
        if (index) putchar(',');
        fputs("{\"relay_ref\":", stdout);
        json_string(devices[index].relay_ref);
        fputs(",\"label\":", stdout);
        json_string(devices[index].product);
        fputs(",\"product\":", stdout);
        json_string(devices[index].product);
        fputs(",\"device_version\":", stdout);
        json_string(device_version(devices[index].release_number));
        printf(",\"release_number\":%ld,\"opened\":true,\"online\":true}", devices[index].release_number);
    }
    puts("]}");
    free(devices);
    IOHIDManagerClose(manager, kIOHIDOptionsTypeNone);
    CFRelease(manager);
    return 0;
}

static bool frame_for_status(const char *status, uint8_t frame[REPORT_SIZE]) {
    uint8_t channel = 0;
    uint8_t action = 0;
    if (strcmp(status, "idle") == 0) {
        channel = 0x01;
        action = 0x01;
    } else if (strcmp(status, "running") == 0) {
        channel = 0x02;
        action = 0x01;
    } else if (strcmp(status, "human_gate") == 0) {
        channel = 0x02;
        action = 0x02;
    } else if (strcmp(status, "error") == 0) {
        channel = 0x04;
        action = 0x02;
    } else {
        return false;
    }
    memset(frame, 0, REPORT_SIZE);
    frame[0] = 0x5E;
    frame[1] = 0x5E;
    frame[2] = 0x06;
    frame[3] = 0x04;
    frame[4] = channel;
    frame[5] = action;
    frame[6] = 0x01;
    frame[7] = 0xFF;
    for (size_t index = 2; index <= 7; index++) frame[8] ^= frame[index];
    return true;
}

static int print_frame(const char *status) {
    uint8_t frame[REPORT_SIZE];
    if (!frame_for_status(status, frame)) return 2;
    for (size_t index = 0; index < REPORT_SIZE; index++) printf("%02x", frame[index]);
    putchar('\n');
    return 0;
}

static int signal_device(const char *relay_ref, const char *status) {
    uint8_t frame[REPORT_SIZE];
    if (!frame_for_status(status, frame)) {
        puts("{\"ok\":false,\"status\":\"unsupported-status\"}");
        return 2;
    }
    IOHIDManagerRef manager = create_manager();
    if (!manager) {
        puts("{\"ok\":false,\"status\":\"manager-unavailable\"}");
        return 3;
    }
    PromLightDevice *devices = NULL;
    size_t count = copy_devices(manager, &devices);
    IOHIDDeviceRef target = NULL;
    for (size_t index = 0; index < count; index++) {
        if (strcmp(devices[index].relay_ref, relay_ref) == 0) {
            target = devices[index].device;
            break;
        }
    }
    int exit_code = 0;
    if (!target) {
        puts("{\"ok\":false,\"status\":\"no-device\"}");
        exit_code = 4;
    } else {
        IOReturn opened = IOHIDDeviceOpen(target, kIOHIDOptionsTypeNone);
        IOReturn written = opened == kIOReturnSuccess
            ? IOHIDDeviceSetReport(target, kIOHIDReportTypeOutput, REPORT_ID, frame, REPORT_SIZE)
            : opened;
        if (opened == kIOReturnSuccess) {
            IOHIDDeviceClose(target, kIOHIDOptionsTypeNone);
        }
        if (written == kIOReturnSuccess) {
            puts("{\"ok\":true,\"status\":\"submitted\",\"verified\":false}");
        } else {
            puts("{\"ok\":false,\"status\":\"write-failed\"}");
            exit_code = 5;
        }
    }
    free(devices);
    IOHIDManagerClose(manager, kIOHIDOptionsTypeNone);
    CFRelease(manager);
    return exit_code;
}

int main(int argc, char **argv) {
    if (argc == 2 && strcmp(argv[1], "list") == 0) return list_devices();
    if (argc == 3 && strcmp(argv[1], "frame") == 0) return print_frame(argv[2]);
    if (argc == 4 && strcmp(argv[1], "signal") == 0) {
        return signal_device(argv[2], argv[3]);
    }
    fputs("usage: promlight-helper list | frame <status> | signal <device> <status>\n", stderr);
    return 2;
}
