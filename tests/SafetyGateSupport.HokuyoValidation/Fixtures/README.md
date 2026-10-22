# UAM status regression fixtures

Derived from the user-provided obstruction and power-only bench reports. The first 13 AR00 status-data characters and the frame length are retained; remaining payload bytes are zeroed and CRCs regenerated. Actual range measurements, timestamps, identity data, report paths and usernames are not included. These fixtures exercise captured status decoding and session behavior; they do not validate physical scanner function.

- obstruction: original status prefix `0001450111100`; original frame SHA-256 `4536f0492137442fb64826576e3ab7f15efac75c8dbfef24a90d8693feedfcec`.
- working: original status prefix `0000000000000`; original frame SHA-256 `e08d442d4cb3aaad4d7040c67cddb004bb53e288a4a8350f58140e3c7e6e5b65`.
- bench-clear: original status prefix `0000000111100`; original frame SHA-256 `8f4121a6f3c1073c9fb5f85794c6da15a0d918d4a88836266f750932b0a0a18e`.
