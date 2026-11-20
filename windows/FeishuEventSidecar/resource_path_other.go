//go:build !windows

package main

import (
	"errors"
	"os"
	"path/filepath"
	"strings"
)

// Root-relative handles keep non-Windows tests portable. Each ancestor is
// checked before opening and its identity checked again through the new handle.
func openSecureResource(path string, create bool) (*secureResourceFile, error) {
	if !validResourcePath(path) {
		return nil, errors.New("invalid resource path")
	}
	root, err := os.OpenRoot(string(filepath.Separator))
	if err != nil {
		return nil, err
	}
	parts := strings.Split(strings.TrimPrefix(path, string(filepath.Separator)), string(filepath.Separator))
	for _, part := range parts[:len(parts)-1] {
		info, err := root.Lstat(part)
		if err != nil || !info.IsDir() || info.Mode()&os.ModeSymlink != 0 {
			root.Close()
			return nil, errors.New("unsafe ancestor")
		}
		next, err := root.OpenRoot(part)
		root.Close()
		if err != nil {
			return nil, err
		}
		root = next
		actual, err := root.Stat(".")
		if err != nil || !os.SameFile(info, actual) {
			root.Close()
			return nil, errors.New("ancestor changed")
		}
	}
	leaf := parts[len(parts)-1]
	var original os.FileInfo
	if !create {
		original, err = root.Lstat(leaf)
		if err != nil || !original.Mode().IsRegular() {
			root.Close()
			return nil, errors.New("unsafe source")
		}
	}
	flags := os.O_RDONLY
	if create {
		flags = os.O_WRONLY | os.O_CREATE | os.O_EXCL
	}
	file, err := root.OpenFile(leaf, flags, 0600)
	if err != nil {
		root.Close()
		return nil, err
	}
	actual, err := file.Stat()
	if err != nil || !actual.Mode().IsRegular() || original != nil && !os.SameFile(original, actual) {
		file.Close()
		root.Close()
		return nil, errors.New("source changed")
	}
	return &secureResourceFile{File: file, finish: func(keep bool) error {
		var removeErr error
		if create && !keep {
			current, err := root.Lstat(leaf)
			if err == nil && os.SameFile(actual, current) {
				removeErr = root.Remove(leaf)
			}
		}
		return errors.Join(removeErr, file.Close(), root.Close())
	}}, nil
}
