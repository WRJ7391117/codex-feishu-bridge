package main

import (
	"context"
	"testing"
	"time"
)

func TestMenuCancellationIsTargetedAndPreventsLaterWork(t *testing.T) {
	runner := newCancelableMenuCommands()
	defer runner.close()
	started, stopped := make(chan struct{}), make(chan struct{})
	if !runner.start(context.Background(), "one", func(ctx context.Context) {
		close(started)
		<-ctx.Done()
		close(stopped)
	}) {
		t.Fatal("first command rejected")
	}
	<-started
	if runner.start(context.Background(), "two", func(context.Context) { t.Error("concurrent menu mutation") }) {
		t.Fatal("menu commands must serialize")
	}
	runner.cancel("other")
	select {
	case <-stopped:
		t.Fatal("wrong request canceled operation")
	default:
	}
	runner.cancel("one")
	select {
	case <-stopped:
	case <-time.After(time.Second):
		t.Fatal("revocation did not reach active operation")
	}
}
