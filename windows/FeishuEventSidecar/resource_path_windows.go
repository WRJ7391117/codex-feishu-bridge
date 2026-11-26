//go:build windows

package main

import (
	"errors"
	"os"
	"path/filepath"
	"syscall"
	"unsafe"
)

var setResourceFileInformation = syscall.NewLazyDLL("kernel32.dll").NewProc("SetFileInformationByHandle")

func openSecureResource(path string, create bool) (*secureResourceFile, error) {
	if !validResourcePath(path) {
		return nil, errors.New("invalid resource path")
	}
	var directories []syscall.Handle
	closeDirectories := func() {
		for i := len(directories) - 1; i >= 0; i-- {
			_ = syscall.CloseHandle(directories[i])
		}
	}
	var ancestors []string
	for parent := filepath.Dir(path); ; parent = filepath.Dir(parent) {
		ancestors = append(ancestors, parent)
		if filepath.Dir(parent) == parent {
			break
		}
	}
	// Open root to leaf, without FILE_SHARE_DELETE: checked directories cannot
	// be renamed/replaced while the transfer owns these handles.
	for i := len(ancestors) - 1; i >= 0; i-- {
		name, err := syscall.UTF16PtrFromString(ancestors[i])
		if err != nil {
			closeDirectories()
			return nil, err
		}
		h, err := syscall.CreateFile(name, 0x80, syscall.FILE_SHARE_READ|syscall.FILE_SHARE_WRITE, nil,
			syscall.OPEN_EXISTING, syscall.FILE_FLAG_BACKUP_SEMANTICS|syscall.FILE_FLAG_OPEN_REPARSE_POINT, 0)
		if err != nil {
			closeDirectories()
			return nil, err
		}
		directories = append(directories, h)
		var info syscall.ByHandleFileInformation
		if syscall.GetFileInformationByHandle(h, &info) != nil || info.FileAttributes&syscall.FILE_ATTRIBUTE_REPARSE_POINT != 0 || info.FileAttributes&syscall.FILE_ATTRIBUTE_DIRECTORY == 0 {
			closeDirectories()
			return nil, errors.New("unsafe resource ancestor")
		}
	}
	name, err := syscall.UTF16PtrFromString(path)
	if err != nil {
		closeDirectories()
		return nil, err
	}
	access, disposition := uint32(syscall.GENERIC_READ), uint32(syscall.OPEN_EXISTING)
	if create {
		access, disposition = syscall.GENERIC_WRITE|0x10000, syscall.CREATE_NEW
	}
	h, err := syscall.CreateFile(name, access, syscall.FILE_SHARE_READ, nil, disposition,
		syscall.FILE_FLAG_OPEN_REPARSE_POINT|syscall.FILE_FLAG_BACKUP_SEMANTICS, 0)
	if err != nil {
		closeDirectories()
		return nil, err
	}
	file := os.NewFile(uintptr(h), path)
	finish := func(keep bool) error {
		var deleteErr error
		if create && !keep {
			// FILE_DISPOSITION_INFO targets our open file, not a potentially
			// substituted pathname. Only handles created with CREATE_NEW enter here.
			remove := uint32(1)
			ok, _, callErr := setResourceFileInformation.Call(uintptr(h), 4, uintptr(unsafe.Pointer(&remove)), unsafe.Sizeof(remove))
			if ok == 0 {
				deleteErr = callErr
			}
		}
		closeErr := file.Close()
		closeDirectories()
		return errors.Join(deleteErr, closeErr)
	}
	var info syscall.ByHandleFileInformation
	fileType, typeErr := syscall.GetFileType(h)
	if syscall.GetFileInformationByHandle(h, &info) != nil || typeErr != nil || fileType != syscall.FILE_TYPE_DISK || info.FileAttributes&(syscall.FILE_ATTRIBUTE_REPARSE_POINT|syscall.FILE_ATTRIBUTE_DIRECTORY) != 0 {
		_ = finish(!create)
		return nil, errors.New("resource is not a regular file")
	}
	return &secureResourceFile{File: file, finish: finish}, nil
}
