# Siemens 8WD46 USB investigation

The standalone `tools/SiemensUsbCheck` diagnostic is the first hardware-validation
step toward independent configuration transfer. Version 0.2.0 adds experimental
upload with durable backup and verification. Physical upload validation is still
pending. The main console's existing Siemens application launcher is unchanged.

## Evidence and limits

The user's Configurator v1.1.17 executable was reconstructed from seven parts and
matched the supplied SHA-256:

`1918e312c71ff4497efb0bbce84df9eacd8e43cc4d8829735a71b072fd3333b4`

Its .NET bundle was extracted and inspected statically. The executable and vendor
assemblies were never run. No proprietary binaries, decompiled vendor sources, or
the user's real configuration are included in this repository. The implementation
here was independently authored from observed behavior. No official protocol
specification was available in this investigation; this does not establish that
Siemens provides no supported interface.

Observed settings: normal USB VID 303A / PID 80F7 (bootloader 80F4 is excluded),
virtual COM at 576000 baud, 8 data bits, no parity, 1 stop bit, no handshake, DTR
and RTS enabled. Windows uses the existing serial driver. Opening those control
lines may restart the device, so use a spare disconnected from operating equipment.

The vendor uses HTTP/1.0 framing over serial. The diagnostic sends these two
requests, in order, only after explicit user action:

```text
GET /api/v1/info HTTP/1.0\r\n
Content-Length: 0\r\n
Connection: keep-alive\r\n
\r\n
```

The second request uses `/api/v1/config` with identical headers. A configuration
read is gated on model `8WD4613-5JH37` (including legacy article `657.101.55` and
the exact compact legacy article `65710155`) and
hardware variant 21. `firstConfig` is retained without using it as a gate: the
vendor's read flow requests configuration regardless of that flag.

The observed vendor upload sequence uses PUT `/api/v1/config`, followed by an
application reset after a delay. Those requests are deliberately absent from the
read-only diagnostic API. The separate upload session implements the observed
configuration PUT and normal application restart only. Cloud and CI tests do not
connect physical USB hardware.

The first user-run hardware report on October 19, 2026 confirmed a real identity
read: firmware v1.0.3, hardware variant 21, numeric article `65710155`, and an
HTTP/1.1 200 reply with chunked transfer encoding. Its content type was `text/html`
even though the body was JSON. Version 0.1.0 rejected that compact article before
requesting configuration. Version 0.1.1 recognizes this exact spelling, as a number
or string, while retaining the article-plus-variant gate. There is no rounding,
exponent conversion, or arbitrary punctuation stripping. That report contained
`firstConfig: false`; without a configuration read it does not establish whether
the column has been programmed. Subsequent user-run reads verified complete
schema-5 backups from two different units. Their initial 65 JSON files were
identical; only ZIP timestamps differed. A later known-programmed configuration
on the second unit contained five active states and still reported `firstConfig:
false`. It must not be used to identify a blank column. Physical upload remains
unverified.

## Version 0.2.0 upload flow

The upload file is an immutable, validated snapshot. This initial tool limits
upload archives to 1 MiB and supports the observed model/variant and firmware
v1.0.3. It rejects incompatible files, unknown firmware and missing stable device
identity before writing.

The session reads identity and configuration, validates the recovery archive,
persists a standalone `.before.signal` with `Flush(true)`, and atomically saves
the report. Only then can it send PUT `/api/v1/config` with the selected ZIP bytes
unchanged. A durable write-intent checkpoint marks an interrupted session as
potentially changed, including when an acknowledgement is lost. Writes are never
automatically retried and no automatic rollback is attempted.

After a successful reply and one-second settling delay, it verifies a configuration
GET against every JSON entry, ignoring archive timestamps/compression and JSON
object ordering. Only matching settings proceed to PUT
`/api/v1/system/reset/app`. That request expects no HTTP response; synchronous write
completion alone is not treated as verified upload. The port is closed, the same
normal USB instance is reopened (its COM name may change), and identity including
hardware MAC is checked before another configuration GET. Final success requires
matching settings after reconnect. Application restart is requested; there is no
uptime field proving a reboot or physical output validation.

Reports and existing recovery files are never overwritten. A disk/checkpoint
failure before PUT blocks writing. The separate backup remains untouched even if
later report updates fail. Cancellation, malformed/non-success replies, unexpected
readback or device changes return an unverified result with available evidence.
The user can explicitly select the recovery `.before.signal` for a later restore.

## Configuration format

The supplied `.signal` file is a ZIP with `configurator.conf` and
`state_config_0.json` through `state_config_63.json`. Schema 5 records device
variant, segment count and sound capability. Supported variant mappings observed
in the application are 20/21 for nine segments without/with sound, and 50/51 for
fifteen segments without/with sound. The initial diagnostic gates on variant 21.

The backup is kept byte-for-byte. Editor color values can differ from expanded
device state values, so reconstructing payloads from editor colors would be
incorrect without reproducing and verifying the transformation. The archive
inspector validates the supported structure, bounds, state values and ZIP CRCs;
unknown schemas are reported as unsupported while preserving the response bytes.

## Validation

`bash scripts/validate.sh` cross-compiles both WPF applications and runs the
existing data/search checks plus dependency-free protocol, identity and archive
checks. These cover fragmented replies, exact request bytes, malformed framing,
length/chunk limits, cancellation, preserved error statuses, ZIP traversal and CRC
failures, model mismatches, and synthetic configuration states. Upload checks use
strict fragmented duplex streams to verify exact request bytes, backup persistence
before the first PUT byte, pre/post-restart readback, same-device verification,
disk failures, lost acknowledgements, cancellations, and absence of write retries.

Native Windows checks additionally render the tool and run its actual session and
report writer against a strict scripted stream. They do not connect physical USB
hardware. The Windows CI build publishes `SiemensUsbCheck-Windows-x64`, including
the .NET runtime and an executable SHA-256 checksum. The ordinary application
artifact is also still available from the same successful run.
