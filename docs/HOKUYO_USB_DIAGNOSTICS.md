# UAM-05LP-T301 USB diagnostics — first hardware validation

Safety Gate Support 6.6.0 adds **Check UAM scanner…** to a unit's UAM scanner resource and to its guided scanner troubleshooting. This is a read-only diagnostic prototype, awaiting comparison against an actual USB response. It does not program the scanner, change its operating mode, reset errors, clear history, or start continuous measurement.

## Try the first reading

1. Use the spare UAM-05LP-T301, isolated from operating equipment. Supply its specified external power, connect its USB data cable, and close UAM Project Designer. Its USB driver must already be installed.
2. Open the unit's **Scanner Configuration** resource → **Check UAM scanner…**. Refreshing lists present SLS USB serial devices without opening a port.
3. Select the intended scanner, click **Read USB status and save report…**, and save the ZIP locally. The default is `%LOCALAPPDATA%\SafetyGateSupport\HokuyoReports`; Q: is not needed for reports. Confirm the first test reading. Serial open uses 115200, 8N1, DTR and RTS asserted, matching the supplied vendor software.
4. Retain the preliminary result and front-display code. After the Support program releases USB, compare the same scanner's state in Project Designer. Send the generated report ZIP and the observed state for validation. There is no need to upload the full vendor installer again.

The first implementation intentionally does **not** label a scanner “working correctly.” It shows preliminary error and lockout flags, the reported area number, and the last recorded error code. Historical codes remain separate from the active-error flag. Protective-field, interlock, warning and OSSD interpretation still needs validation; an OFF output would not by itself establish a fault. No-fault status is not proof of the scanner's physical protective function.

The separate **Look up a display code** tab works without USB. Its explanations are paraphrased from the supplied *UAM User's Manual C-61-00003-12*, Table 9-2, printed pp. 138–141. It does not change the live USB result. Home Preview permits lookup and disables all USB operations.

## Implementation evidence and limits

Static inspection of the supplied UAM Project Designer native executable identified its SLS serial interface (`VID_143F`, `PID_0021`), connection settings, fixed identity read `VR00` and one-shot measurement/status read `AR00`. Vendor code was neither executed nor incorporated into this repository. Vendor EXEs, DLLs, manuals and user diagnostic captures are not distributed with the app.

Frames use STX/ETX, four uppercase hexadecimal total-length characters and CRC-16 with reflected polynomial 0x8408, zero initial value and no final XOR or byte swap. The native implementation independently yields `000EVR00` → `3492`. Replies require matching command, valid length, printable ASCII, CRC, and protocol result `00`. The model must be the exact `UAM-05LP-T301` comma-separated identity field before `AR00` is sent. A different identity format is retained for review, never accepted using a loose substring.

The common LP monitor prefix is mode(1), area(2 hex), active error(1), last error(2 hex), lockout(1). Native readers consume active error and last error separately. This initial decoder only presents that prefix provisionally; it does not infer output causes from unvalidated trailing fields. No real hardware capture has yet verified this implementation's framing or interpretation.

At most one identity query and one status query are sent. No login, retry, baud cycling, reset, configuration, arbitrary-command input or streaming controls exist. Each declared response is bounded to 65,535 bytes. A 40-second cooperative session deadline and serial read/write limits bound the operation. Invalid/partial replies, rejection, cancellation, wrong model or failed port closure leave the result unverified. The report is reserved before opening USB; existing reports are never overwritten. Raw request/response bytes and a timestamped result are saved locally, including partial responses on failure. A report persistence failure is surfaced to the user.

Automated fixtures verify failure handling and framing only. They cannot validate the scanner's physical behavior or replace the first USB comparison and the site's approved functional test.
