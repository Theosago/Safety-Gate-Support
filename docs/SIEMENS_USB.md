# Siemens 8WD46 USB investigation

The standalone `tools/SiemensUsbCheck` diagnostic is the first hardware-validation
step toward independent configuration transfer. Production upload support is not
implemented. The main console's existing Siemens application launcher is unchanged.

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
read is gated on model `8WD4613-5JH37` (including legacy article `657.101.55`) and
hardware variant 21. `firstConfig` is retained without using it as a gate: the
vendor's read flow requests configuration regardless of that flag.

The observed vendor upload sequence uses PUT `/api/v1/config`, followed by an
application reset after a delay. Those requests are deliberately absent from the
diagnostic API. Before implementing them, validate identity, real response framing,
backup contents, and firmware behavior with the spare column and a known-good
configuration. No real USB session has been exercised by cloud or CI tests.

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
failures, model mismatches, and synthetic configuration states.

Native Windows checks additionally render the tool and run its actual session and
report writer against a strict scripted stream. They do not connect physical USB
hardware. The Windows CI build publishes `SiemensUsbCheck-Windows-x64`, including
the .NET runtime and an executable SHA-256 checksum. The ordinary application
artifact is also still available from the same successful run.
