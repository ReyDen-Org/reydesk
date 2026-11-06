// Command rendezvous is the ReyDesk rendezvous server.
package main

import (
	"context"
	"errors"
	"fmt"
	"log/slog"
	"net/http"
	"os"
	"os/signal"
	"syscall"
	"time"

	"github.com/jackc/pgx/v5/pgxpool"

	"reydesk/rendezvous/internal/auth"
	"reydesk/rendezvous/internal/config"
	"reydesk/rendezvous/internal/db"
	"reydesk/rendezvous/internal/hub"
	"reydesk/rendezvous/internal/ice"
	"reydesk/rendezvous/internal/limits"
	"reydesk/rendezvous/internal/logx"
)

func main() {
	// "rendezvous healthcheck" is used by the Docker HEALTHCHECK (distroless has no curl).
	if len(os.Args) > 1 && os.Args[1] == "healthcheck" {
		os.Exit(healthcheck())
	}
	if err := run(); err != nil {
		slog.Error("fatal", "event", "fatal", "err", err.Error())
		os.Exit(1)
	}
}

func healthcheck() int {
	addr := os.Getenv("LISTEN_ADDR")
	if addr == "" {
		addr = ":8080"
	}
	if addr[0] == ':' {
		addr = "127.0.0.1" + addr
	}
	c := http.Client{Timeout: 3 * time.Second}
	resp, err := c.Get("http://" + addr + "/api/health")
	if err != nil {
		return 1
	}
	defer resp.Body.Close()
	if resp.StatusCode != http.StatusOK {
		return 1
	}
	return 0
}

func run() error {
	cfg, err := config.Load()
	if err != nil {
		return err
	}
	slog.SetDefault(slog.New(slog.NewJSONHandler(os.Stdout, &slog.HandlerOptions{Level: cfg.LogLevel})))

	ctx, stop := signal.NotifyContext(context.Background(), os.Interrupt, syscall.SIGTERM)
	defer stop()

	pool, err := connectWithRetry(ctx, cfg.DatabaseURL)
	if err != nil {
		return err
	}
	defer pool.Close()
	if err := db.Migrate(ctx, pool); err != nil {
		return err
	}

	if cfg.PublicWSURL == "" {
		return errors.New("PUBLIC_WS_URL is required (it is bound into auth signatures)")
	}
	reclaimUntil, err := auth.ParseReclaimUntil(cfg.ReclaimUntil)
	if err != nil {
		return fmt.Errorf("RECLAIM_UNTIL: %w", err)
	}
	authQ := db.AuthQueries{Pool: pool}
	limiter := limits.New(nil)
	logger := logx.New(slog.Default(), nil)
	h := hub.New(hub.Config{Limiter: limiter, Log: logger})
	ws := hub.NewServer(hub.ServerConfig{
		Hub:      h,
		Verifier: auth.New(auth.Config{Store: authQ, Limiter: limiter, PublicWSURL: cfg.PublicWSURL, ReclaimUntil: reclaimUntil}),
		Limiter:  limiter,
		Ice: &ice.Config{
			STUNURLs: cfg.STUNURLs, TURNURLs: cfg.TURNURLs, Secret: cfg.TURNSecret, Enabled: cfg.TURNRelayEnabled,
			TTL: time.Duration(cfg.TURNTTLSeconds) * time.Second,
		},
		Log:            logger,
		TrustedProxies: cfg.TrustedProxyCIDRs,
		MaxConnections: cfg.MaxConnections,
	})
	go every(ctx, time.Minute, limiter.Sweep)
	go h.Janitor(ctx, 10*time.Second)
	go func() {
		revokeStale := func() {
			n, err := authQ.RevokeStale(ctx, auth.StaleAfter)
			if err != nil {
				logger.Warn("revoke_stale", logx.Fields{Result: "error"})
			} else if n > 0 {
				logger.Info("revoke_stale", logx.Fields{Result: "ok"})
			}
		}
		revokeStale()
		every(ctx, 24*time.Hour, revokeStale)
	}()

	mux := http.NewServeMux()
	mux.HandleFunc("GET /api/health", healthHandler(pool))
	mux.Handle("GET "+hub.Endpoint, ws)
	srv := &http.Server{
		Addr:              cfg.ListenAddr,
		Handler:           mux,
		ReadHeaderTimeout: 10 * time.Second,
	}
	errc := make(chan error, 1)
	go func() { errc <- srv.ListenAndServe() }()
	slog.Info("listening", "event", "start", "addr", cfg.ListenAddr)

	select {
	case err := <-errc:
		return err
	case <-ctx.Done():
	}
	h.CloseAll() // hijacked websocket connections are not stopped by srv.Shutdown
	sctx, cancel := context.WithTimeout(context.Background(), 10*time.Second)
	defer cancel()
	if err := srv.Shutdown(sctx); err != nil && !errors.Is(err, http.ErrServerClosed) {
		return err
	}
	return nil
}

// every runs fn each interval until ctx ends.
func every(ctx context.Context, interval time.Duration, fn func()) {
	t := time.NewTicker(interval)
	defer t.Stop()
	for {
		select {
		case <-ctx.Done():
			return
		case <-t.C:
			fn()
		}
	}
}

// connectWithRetry waits for the database at startup (compose starts both together).
func connectWithRetry(ctx context.Context, url string) (*pgxpool.Pool, error) {
	var lastErr error
	for i := 0; i < 30; i++ {
		pool, err := db.Connect(ctx, url)
		if err == nil {
			return pool, nil
		}
		lastErr = err
		slog.Warn("database not ready", "event", "db_wait")
		select {
		case <-ctx.Done():
			return nil, ctx.Err()
		case <-time.After(2 * time.Second):
		}
	}
	return nil, lastErr
}

func healthHandler(pool *pgxpool.Pool) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		ctx, cancel := context.WithTimeout(r.Context(), 2*time.Second)
		defer cancel()
		w.Header().Set("Content-Type", "application/json")
		w.Header().Set("Cache-Control", "no-store")
		if err := pool.Ping(ctx); err != nil {
			w.WriteHeader(http.StatusServiceUnavailable)
			_, _ = w.Write([]byte(`{"status":"unavailable"}`))
			return
		}
		_, _ = w.Write([]byte(`{"status":"ok"}`))
	}
}
