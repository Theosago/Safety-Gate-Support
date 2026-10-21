# Hokuyo UAM-05LP-T301 — USB diagnostic investigation

The user identified **UAM-05LP-T301**, connected by **USB**, and supplied
`UAM Project Designer.zip`. Its size exceeds the cloud executor's **32 MiB**
attachment transfer limit; the archive has **not** been downloaded or inspected.
No USB status commands, VID/PID, endpoint, driver or error-code meanings have yet
been established from that software. No independent UAM live diagnostics have been
added to Safety Gate Support at this stage.

## Available evidence

The supplied site troubleshooting guide references UAM Project Designer over
Micro-USB. The earlier `Omni Sensors` archive contains `.hux` configuration files,
not the Project Designer software or its USB protocol implementation.

A public [UAM-05LP-T301 prototype](https://github.com/shogo0x2e/HokuyoUam05lpt301Prototype)
and related [Unity project](https://github.com/shogo0x2e/HokuyoUam05lpForUnity)
were inspected as secondary references. Their documented transport is **TCP over
Ethernet**, not the user's USB connection. Their transport behavior and status
fields do not establish the USB configuration/diagnostic protocol. No source code
from those projects was copied into this repository or executed.

Manufacturer documentation could not be fetched through this workspace's network
policy (HTTP CONNECT 403). These access limits do not establish that an official
USB API or diagnostic specification is unavailable.

## Collect the required software files

The standalone [UAM software pack collector](../tools/UamSoftwarePack/README.md)
reads the original ZIP on the user's PC and creates an analysis upload below
30 MiB, prioritizing the main program, vendor USB/communication libraries, driver
INF files and diagnostic resources. It includes a filename/size inventory of
omitted entries so large installers or missing dependencies can be requested
precisely. It never executes vendor code or opens a scanner.

Its dependency-free collector tests run on Linux and Windows; a separate workflow
publishes and opens the self-contained Windows GUI before uploading the artifact.
Vendor binaries and the eventual analysis upload must stay out of source control.

## Next integration work

After the small pack is available, statically inspect its executable metadata,
USB API calls, driver/interface identifiers, read-status command construction,
response framing, error tables, and connection side effects. Establish the actual
USB transport before implementing device discovery or communication. Do not send
SCIP commands or assume that an Ethernet command can be sent over this USB link.

A first hardware check should be restricted to confirmed identity/status reads
with bounded response parsing, cancellation, an explicit user-selected device,
and raw diagnostic reports. Check replies against Project Designer on equipment
prepared for testing. Interpret disconnected, unknown, fault, and no-reported-fault
states separately; a successful USB connection alone must never be shown as
"scanner working correctly." Avoid changing detection fields, writing scanner
configuration, resetting faults, or altering OSSD behavior in a status feature.
