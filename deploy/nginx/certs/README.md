# nginx/certs (generated, git-ignored)

Run `./generate-dev-certs.sh` from this directory to produce `tls.crt` and
`tls.key` here — self-signed, `CN=localhost`, for the compose stack's dev
nginx TLS listener. `deploy/nginx/conf.d/default.conf` *references* these two
generic filenames at `/etc/nginx/certs/tls.{crt,key}` (`compose.yaml` does
the mounting), so the identical config serves either this dev-generated pair
or an operator's real certificate with no edit.

Both files are git-ignored and must never be committed. In production, the
operator supplies real certificates from their internal CA — this script has
no bearing on that path (see `deploy/README.md`'s var-reference table for the
`tls-cert-file`/`tls-key-file` anchors).
