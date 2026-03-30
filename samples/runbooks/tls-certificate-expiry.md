# TLS certificate expiry

Alert: `CertificateExpiresSoon` fires 14 days before a certificate managed by cert-manager expires.

## Diagnosis

`kubectl get certificate -A` shows `READY=False` for certificates that failed to renew. Then
`kubectl describe certificaterequest` and the cert-manager logs show the ACME challenge error.

## Mitigation

- HTTP-01 challenges fail when the ingress does not route `/.well-known/acme-challenge/` to the solver;
  check the ingress class annotation.
- DNS-01 challenges fail when the DNS provider credentials secret has expired; rotate it.
- As a last resort, delete the failed CertificateRequest so cert-manager retries immediately.
