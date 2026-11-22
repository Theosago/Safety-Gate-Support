# Publish unit guides and print QR codes

## Start publishing

1. Open **Publish & print QR** in the main sidebar. The app reuses the Google
   sign-in and the Drive folder from the previous pilot on the publishing PC.
   If Google needs you to reconnect, select **Sign in with Google**. On a new
   PC, choose the Google Desktop app OAuth JSON once. The app stores a Windows
   user-encrypted session.
2. Select **Publish all units**. The app creates **Safety Gate Operator PDFs**
   in My Drive if there is no saved publishing folder. Under it, the app creates
   one folder per asset and unit, including units waiting for their first drawing.
   Libraries with an existing folder can select it once under **Google Drive
   settings** before their first publication.
3. Use **Open Drive folder** to check the result. If the folder is new and
   operators should view it while signed out, set **Anyone with the link** on
   this one parent folder. Unit folders inherit its access.
4. Select **Print all QR codes…** after all folders are ready. The app creates
   one US Letter PDF and opens it so you can print every label together.
   Print at actual size. Each label opens its unit folder in a phone browser.

## Update a unit

Select the unit in the regular app, then choose **Upload drawing pair…** to
replace the displayed sheet or add its first pair. Choose the matching PDF and
DWG together. To add a separate sheet while keeping the displayed sheet, use
**Drawing files → Add PDF + DWG…**. The app saves both companions to the shared
library and publishes the PDF to Drive automatically. A signed-in publisher
running with the library connected checks pending changes about every 30 seconds.
It also picks up changes from coworkers who saved pairs on another workstation.

The unit folder contains the standalone electrical drawing, a **Unit guide**
PDF, and **Operator notes**. The guide starts with the unit's identification photo,
asset and unit numbers, location, equipment details, and a linked contents list.
The first assigned drawing starts the drawing section unless a previously
published main drawing is already linked. Additional assigned drawing PDFs
follow it. Use **Add a PDF to a unit guide** in the publishing window to attach
an optional document, such as an exported signal column configuration. Attach
the same filename again to replace that attachment. Supplemental PDFs appear
as separate sections in the guide. DWG files and desktop shared notes stay in
the engineering library.

## Existing links and recovery

Published unit folders and existing PDFs are addressed by their Drive IDs.
Changing a drawing's filename or publishing another revision keeps the printed
folder QR and any direct links to the primary PDF working. The app leaves
the Operator notes document and its sharing permissions in Drive.

If Drive is unavailable, the drawing pair stays saved in the library. The
publishing window shows individual issues, and the app retries. If someone
changes a generated PDF directly in Drive, review and restore the last
published revision there before automatic publishing can overwrite it.
The app leaves Drive and engineering files in place when units are removed.

The app keeps publishing mappings in the shared library under
`_AppData/Operator Publishing/publishing.json`. Keep this file with the
library if you move or restore it. After upgrading from the pilot, open the
library on the original pilot PC first so it can import the saved Drive IDs.

## Google account setup on a new installation

Enable the Google Drive API in your Google Cloud project and create a
**Desktop app** OAuth client. If the app is **External** and still in
**Testing**, add the publishing account to **Test users**. Google can expire
those test refresh tokens after seven days. Use **Sign in with Google** to
reconnect using the previously saved client. Keep the credential JSON local.
Existing Shared Drive publishers can instead use a configured service account
with access to their existing folder through `GOOGLE_APPLICATION_CREDENTIALS`.

The publisher requests Drive access to create unit folders and update existing
files by ID. The workstation needs access to Google sign-in,
`oauth2.googleapis.com` and `www.googleapis.com`. Google Drive determines
browser access and notes editing from the folder and document sharing settings.
