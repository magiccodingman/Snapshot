# Hosting and ETags

A correct archive can still be served incorrectly.

The hosted-site validator requests every canonical snapshot route and records status, content type, length, ETag, cache control, and body SHA-256. It raises `HOST001` when divergent response bodies share an identical ETag.

This detector is provider-neutral. Remediation is not. Each hosting provider needs a dedicated implementation that reflects its actual configuration surface.
