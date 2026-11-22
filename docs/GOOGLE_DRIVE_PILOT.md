# Google Drive operator PDF pilot

Use one Windows workstation and one saved pilot unit. The engineering library
stays on the company’s mapped `Q:` drive. The desktop app uses the workstation’s
Windows permissions to read the library. It publishes only the selected approved
PDF to Google Drive, where operators open the unit folder on their phones.

## Publisher authentication

1. Enable the Google Drive API in an approved Google Cloud project and provision
   a service account under your organization’s credential policy.
2. Give the service account Contributor access to a dedicated Google Shared
   Drive for operator publications. It needs permission to create folders,
   create Google Docs and update PDFs.
3. Set the workstation’s `GOOGLE_APPLICATION_CREDENTIALS` environment variable
   to the absolute path of the service-account JSON before launching the app.
   Keep the JSON outside the repository and the shared library. The app requests
   `https://www.googleapis.com/auth/drive` so it can work with existing files.
4. The workstation needs access to `oauth2.googleapis.com` and
   `www.googleapis.com`. Operators open the unit folder at `drive.google.com`.

## Publish and label one unit

1. In **Plant setup → Start here**, select the saved pilot unit. Prepare an
   approved PDF of up to 25 MiB at a stable path in the shared data folder.
2. Open **Google Drive operator PDF…** under step 5. Choose the approved PDF
   and enter the pilot root folder ID. The app publishes under
   `<root>/Asset <asset>/Unit <unit>/`.
3. If a printed QR already opens a PDF, enter that PDF’s Drive file ID. It must
   already be in the correct unit folder. If the ID is omitted, the app looks
   for an exact filename match and creates the PDF if none exists. The app
   pauses on duplicate names. Start with identical copies when linking an
   existing Drive PDF.
4. Enable publishing and use **Save unit folder QR**. Verify that the QR opens
   the correct asset and unit from a phone.
5. For phone viewing without signing in, have a Drive manager set the approved
   operator folder to **Anyone with the link → Viewer**, where company policy
   permits. Test while signed out. The app leaves sharing permissions as set in Drive.

## Operator notes

The app creates or finds one Google Doc named **Operator notes** in the unit
folder. Operators can open it from the same folder as the PDFs. Grant the
operators’ Google accounts or group **Editor** access on that document only.
They sign in to edit notes, while the approved PDFs remain view-only. Folder
view access can also expose the notes document to link holders. Use it for notes
approved for that audience. The app leaves the document’s contents to operators.

## Changes and recovery

The app checks every 30 seconds while running with the shared library connected.
**Sync now** checks immediately. Use **Pause sync** to stop automatic publishing.

- A change to the selected PDF on `Q:` updates the existing Drive file ID.
  Existing direct-to-PDF labels continue working. The folder QR is independent
  of individual PDF revisions.
- Drive PDF changes never write to `Q:`. If the Drive PDF was edited separately,
  the app preserves both copies and pauses the update. Review the Drive revision
  and restore the last published revision there or deliberately reconcile the
  two copies before resuming.
- The app never propagates deletions. Restore access to the original item if a
  bound file, folder or notes document is missing, moved or trashed.
- Pilot mappings live in
  `%LOCALAPPDATA%\SafetyGateSupport\operator-drive-pilot.json`. Back up this
  file when moving the pilot workstation. The credential is stored separately.
