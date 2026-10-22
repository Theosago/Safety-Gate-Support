# Safety Gate Support — Home Preview (7.0.0)

1. Extract the entire ZIP into a folder on your Windows PC.
2. Double-click **SafetyGateSupportPreview.exe**.
3. Under **Just need a file?**, click **Light configurations**, search **DEMO**, select **Unit 1**, and click **Continue**. Click **Check / configure light**.

Choose **DEMO ONLY - not for equipment.signal** in the assigned-files list. Use the **Expected lights & sound** tab. Turn on only **Input 1** (red), **Input 2** (amber), **Input 3** (green), or **Input 4** (sound); turn the others off. The preview displays stored values; it does not animate or play physical outputs. Device discovery, USB reads and uploads are disabled in the home preview. The sample configuration is also blocked from upload by the shared upload engine.

Use the six symptom cards on Home or **Troubleshoot** on a selected unit to explore the new signal-column, BEA LZR-S600, UAM scanner and actuator guides. Browse the starter inventory/photos, search, try notes, and open **Support library** to explore importing local documents or ZIPs.

No Q drive, VPN, Siemens software, SDK, or installer is required. This is a self-contained local preview. It uses its own settings and data under:

```
%LOCALAPPDATA%\SafetyGateSupport\HomePreview
```

Your normal app settings and shared data are separate. Local preview edits remain when reopening it. To reset the preview, close it and delete only the `HomePreview` folder above, then reopen the preview executable. The normal app can also be launched with `--preview` to select this local profile.

The inventory/photos are from the bundled starter data, not a live copy of Q. Asset DEMO and its configuration are illustrative. The large plant schematics, manuals, actual signal configurations and vendor programs are not bundled; those still need their original local files for import. Plant stockroom services and real equipment checks are unavailable from this preview.
