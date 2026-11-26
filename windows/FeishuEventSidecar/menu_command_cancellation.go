package main

import (
	"context"
	"sync"
	"time"
)

// Keep decoding stdin while a menu HTTP operation runs, so revoking the App
// session can cancel its context without stopping the Bot or unrelated work.
type cancelableMenuCommands struct {
	mu            sync.Mutex
	id            string
	cancelCurrent context.CancelFunc
	workers       sync.WaitGroup
}

func newCancelableMenuCommands() *cancelableMenuCommands { return &cancelableMenuCommands{} }
func (r *cancelableMenuCommands) start(parent context.Context, id string, run func(context.Context)) bool {
	r.mu.Lock()
	defer r.mu.Unlock()
	if r.id != "" {
		return false
	}
	ctx, cancel := context.WithTimeout(parent, 60*time.Second)
	r.id, r.cancelCurrent = id, cancel
	r.workers.Add(1)
	go func() {
		defer r.workers.Done()
		defer cancel()
		defer func() { r.mu.Lock(); r.id = ""; r.cancelCurrent = nil; r.mu.Unlock() }()
		run(ctx)
	}()
	return true
}
func (r *cancelableMenuCommands) cancel(id string) {
	r.mu.Lock()
	defer r.mu.Unlock()
	if r.id == id && r.cancelCurrent != nil {
		r.cancelCurrent()
	}
}
func (r *cancelableMenuCommands) close() {
	r.mu.Lock()
	if r.cancelCurrent != nil {
		r.cancelCurrent()
	}
	r.mu.Unlock()
	r.workers.Wait()
}
