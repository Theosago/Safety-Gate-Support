# Google Drive operator PDF pilot

Use one Windows workstation and one saved pilot unit. The app’s existing library
continues to organize drawings, configurations, manuals and unit assignments.
Only the approved PDF selected for this pilot syncs with Google Drive.

## Authentication

1. Enable the Google Drive API in an approved Google Cloud project. Provision a
   service account and a service-account JSON credential under your organization’s
   credential policy.
2. Grant the service account access to the test Shared Drive. Contributor access
   (Drive API role `writer`) is sufficient where Shared Drive policy allows the
   account to create folders and read, download and edit the pilot PDF. Grant
   access separately to the operators who will scan the code.
3. Set the Windows environment variable `GOOGLE_APPLICATION_CREDENTIALS` to the
   JSON file’s absolute path before launching the desktop app. Store the JSON
   outside both the repository and the shared data folder. The app requests the
   Google Drive OAuth scope `https://www.googleapis.com/auth/drive` to work with
   pre-existing files and create folders. It uses the service account’s access
   and does not grant public links or change file permissions.
4. The workstation must be able to reach `oauth2.googleapis.com` and
   `www.googleapis.com`. Operators open the links at `drive.google.com` using
   accounts with view access.

## Set up the test unit

1. In **Plant setup → Start here**, select the saved pilot unit and attach or
   prepare an approved operator PDF in the shared data folder. Use a stable
   local path and a PDF of up to 25 MiB.
2. Open **Google Drive operator PDF…** under step 5. Select the shared PDF and
   enter the ID of the pilot root folder in a Google Shared Drive. The app uses
   its saved asset and unit display names to create or find
   `<root>/Asset <asset>/Unit <unit>/`.
3. If a QR label already exists, enter its Drive PDF’s file ID. The file must
   already be in that Asset/Unit folder. If the ID is omitted, the app looks
   for an exact filename match there and creates the PDF if there is none.
   Identical names are allowed in Google Drive. The app pauses if the lookup
   returns more than one item.
4. For an existing PDF, use identical copies for the first sync. Choose
   **Enable sync** and verify the Drive link. Use **Save QR label** to export
   a printable SVG, then check access with an operator’s account.

## Ongoing sync

The app checks every 30 seconds while it is running and connected to the
configured shared library. **Sync now** checks immediately. **Pause sync** stops
automatic checks for the pilot until resumed. Use only one publishing
workstation for the unit.

- A change to the selected shared PDF uploads a new revision to the same Drive
  file ID. Existing Drive links and printed QR labels continue to address that
  file.
- A Drive revision downloads to the selected shared-folder path. The previous
  local PDF is saved in `.operator-pdf-history` next to the selected PDF.
- If both sides change, compare and back up both copies, then put the approved
  bytes on both sides to resume. A missing, moved, trashed or inaccessible bound
  Drive file produces an error. Restore access or the same file to the original
  Asset/Unit folder. The pilot never propagates deletions or creates a replacement
  for a missing bound file.
- Pilot state is stored in
  `%LOCALAPPDATA%\SafetyGateSupport\operator-drive-pilot.json`. Back it up if
  moving the pilot to another workstation. The credential is stored separately.
