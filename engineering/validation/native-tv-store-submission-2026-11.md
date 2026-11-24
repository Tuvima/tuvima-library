# TV store submission: what Shy does by hand (Roku, Samsung, LG)

> Spec: [native-apps](../specs/native-apps.md), WP13 · Written 2026-11-23 · Text to paste: [native-tv-store-listing-2026-11.md](native-tv-store-listing-2026-11.md) · Status: Not started (needs Shy)

The cloud build made the packages, the pictures and the text. Nothing below can be done by a build: each store needs a person to sign in, accept an agreement, press upload and wait for the store's own review. I wrote this from what I knew of each store's process; I could not open the stores' pages from the build, so **every menu name and picture size below is the one to compare with the live page first (Step 0)**. If a store asks for money, stops asking for something listed here, or asks for something not listed, stop and tell me; do not guess.

## Cost and licence at a glance

- Roku: no fee known. Samsung: no fee known. LG: no fee known. TV apps are free for people to use (spec D5, D20).
- Tools you will install on your own computer are used by hand and are not added to the repository: Tizen Studio (Samsung), `ares-cli` (LG, Apache 2.0). Roku needs no tool, only the Node script already in the repository.
- The apps are open source under MPL 2.0 (spec D1). The licence file travels inside each package.

## Step 0, before any store (30 minutes)

1. Open each store's artwork page and compare the picture sizes with the table at the end of the [listing text](native-tv-store-listing-2026-11.md). If a size differs, tell me the new size; the fix is one line in `tools/Make-TvStoreImages.mjs` and one in `tests/build/tv-store-packages.test.mjs`, then `node tools/Make-TvStoreImages.mjs`.
2. Decide the three open items in "Decisions you owe" at the end. Roku can go first without the others.
3. Have a support email ready that is not your personal one.

## A. Roku

Roku is the first store to try: you own a Roku TV, the package builds with no tools, and there is no certificate to buy.

### A1. Account (about 20 minutes, once)
1. Go to developer.roku.com and sign in with (or create) a Roku account. Roku may ask you to turn on two-step verification for a developer account; do so if asked.
2. Accept the Roku developer agreement.
3. If the form asks for a fee or a card, stop and tell me.

### A2. Put the package on your Roku (about 15 minutes)
1. On the Roku remote press: Home three times, Up twice, Right, Left, Right, Left, Right. Developer mode opens. Accept the terms, set a web password you will remember, and let it restart. Note the Roku's IP address on screen.
2. On this computer, from the repository folder, run `node tools/Package-RokuChannel.mjs`. It writes `clients/roku/dist/library-roku.zip` and prints the file count and size.
3. In a browser open `http://<roku-ip>`, sign in (username `rokudev`, the password you set), choose **Upload**, pick the zip, and choose **Install**. The channel opens on the TV. This is also the check that the manifest and pictures are accepted by a real Roku.
4. Pair it with a test Library server on the same network and play something (the Local check for WP6 already covers the player; this one covers that the new pictures and splash show).

### A3. Sign the channel and keep the key safe
1. Roku signs store packages with a key made on your own Roku. Connect to the Roku's debug console (Roku's developer pages describe it: `telnet <roku-ip> 8080`), type `genkey`, and write down the **password** and **Dev ID** it prints. Type `exit`.
2. In the browser page of step A2 open **Packager**, enter the app name `Library`, the password, and choose **Package**. Download the `.pkg`.
3. Keep the password, the Dev ID and the `.pkg` in your password manager. **Never put them in the repository.** If the key is lost you cannot publish updates to the same channel.

### A4. Create the channel and submit
1. In the Roku Developer Dashboard choose **Manage My Channels**, then **Add Channel**. Roku offers a private test (non-certified or beta) channel and a public channel. Do the private one first: it gives you an install link and tests the upload without a public listing.
2. Fill in the channel from the [listing text](native-tv-store-listing-2026-11.md): name Library, description, category, content rating, support email, privacy policy address. Upload the poster and screenshots. Price: free.
3. Upload the `.pkg` from step A3.
4. For the public channel choose **Submit for certification** after Roku's own checklist is ticked.
5. **Before submitting publicly, get Roku's answer on sign-in** (the open question below). Roku's reviewers fail channels on sign-in rules, so submitting first wastes a review round.

### A5. Roku's sign-in question (open, not guessed)
The channel pairs by a code that an administrator approves in the Dashboard, with no account with Roku and no password typed on the TV. I do not know whether Roku's current certification criteria accept that. Ask Roku (Developer Dashboard support, or the certification contact on the channel review page):

> Our channel pairs with the customer's own self-hosted media server. The TV shows a short code and the server's web address; an administrator on another device approves the TV on the server (OAuth device authorization, RFC 8628). There is no Roku-hosted sign-in and no password on the TV. Does this meet your authentication criterion for channels, and is anything more required (for example account linking through Roku, or a particular sign-in screen)?

Write the answer, the date and the criterion's number into `clients/roku/README.md` (section "Open question for Roku: sign-in") and tell me. If Roku says no, that becomes a new work packet and a decision for you.

## B. Samsung (Tizen, Seller Office)

### B1. Accounts and tools (about 2 hours, once)
1. Create a Samsung account (or use yours), then go to Samsung Seller Office (seller.samsungapps.com) and register as a seller. Choose the TV application service when it asks. Individual or company registration: choose what you want shown as the publisher; the listing text uses "Tuvima". If it asks for a fee or business documents you do not have, stop and tell me.
2. Install Tizen Studio on a Windows computer with the TV extensions (Samsung's pages explain which package). It includes the TV emulator and Certificate Manager.
3. In Certificate Manager create a Samsung certificate profile (author and distributor certificates, signed in with the same Samsung account). A distributor certificate for a real TV needs that TV's DUID; for the emulator it does not. Samsung's TV pages say which type a store upload needs; follow them.

### B2. Build and test
1. Before anything else, decide the open item "Samsung and LG need a server setting" below. Do not submit before it is decided.
2. Change the package id: `TuvimaLib0.TuvimaLibrary` in `clients/tv-web/platforms/tizen/config.xml` is a placeholder; Samsung issues the real package id when you create the app in Seller Office. Tell me the id and I change it (it is also in the README and the run command).
3. From the repository folder run `pwsh -NoProfile -File clients/tv-web/platforms/tizen/package.ps1 -Certificate <profile> -Tv <tv-name>`, then run the four `tizen` commands it prints. The result is `Library.wgt`, signed with your certificate.
4. Try it in the TV emulator, or on a real Samsung TV with Developer mode on (the Tizen README has the steps). Check Back, Play, Pause, Rewind and Fast-forward keys.

### B3. Submit
1. In Seller Office choose to add a new application for TV, and upload `Library.wgt`.
2. Fill in the listing from the [listing text](native-tv-store-listing-2026-11.md): description, category, age rating answers, privacy answers, support email, privacy policy address. Upload `clients/tv-web/platforms/tizen/store/store-icon-512x423.png` and your 1920 x 1080 screenshots.
3. In the notes for Samsung's testers use the reviewer text (see "How will the stores test it?" below).
4. Submit for review and watch the email on your Samsung account. Samsung may ask for changes; send me the message and I turn it into a fix.

## C. LG (webOS, Seller Lounge)

### C1. Accounts and tools (about 1 hour, once)
1. Create an LG account and register at LG Seller Lounge (seller.lgappstv.com). If it asks for a fee, stop and tell me.
2. Install the webOS TV command line tools: `npm install -g @webos-tv/ares-cli` (needs Node). Install the webOS TV simulator if you want to try the app without a TV (LG's page "webOS TV SDK").
3. Optional, for a real LG TV: install **Developer Mode** from the LG Content Store, sign in with an LG developer account, switch it on, turn on Key Server (the webOS README has the steps).

### C2. Build and test
1. Decide the open item "Samsung and LG need a server setting" below first.
2. Run `pwsh -File clients/tv-web/platforms/webos/package.ps1`. It builds the folder and prints the `ares-package` command. Run it: `ares-package <build folder> -o dist`. The result is `dist/com.tuvima.library.tv_0.1.0_all.ipk`.
3. Try it in the simulator or on a TV with `ares-install` and `ares-launch` (printed by the script).

### C3. Submit
1. In Seller Lounge add a new app, upload the `.ipk`, and fill in the listing from the [listing text](native-tv-store-listing-2026-11.md). Upload your 1920 x 1080 screenshots; the app icons are inside the package.
2. LG asks the developer to tick a self-check list and may ask for test notes: use the reviewer text below.
3. Submit for LG's test and watch your email. Send me anything LG asks to change.

## Decisions you owe

1. **How will the stores test it?** A reviewer at Roku, Samsung or LG cannot reach a Library server on your home network, and without a server the app shows only the "type your server address" screen. Options: (a) run a small demo Library on the public internet just for reviewers, with a test administrator account, a few public-domain videos, and native app access on (cost: a small always-on machine or hosted server and a short-lived public address, which is the only place this project would put a server on the internet, and it needs the explicit remote-access opt-in from the Remote access spec); (b) give reviewers a screen recording of the pairing and playback plus the explanation that it is a player for the user's own server, and hope the store accepts it; (c) skip the stores for now and ship only by sideload. Recommended: (a) for Roku first, because Roku is the one you can test yourself, then learn from its reaction before Samsung and LG.
2. **Samsung and LG need a server setting (now a Settings switch).** The packaged Samsung and LG apps run from a non-web origin, so the server refuses them until an administrator turns on "Allow Samsung and LG TV apps" in Settings > Network & Remote Access > Home network (off by default; applies right away; no file edit). Decided: the Settings switch was built first (PR "Settings: switch for Samsung and LG TV apps"). Still to do: say in the listing and first-run help to turn it on before pairing, and check it on a Samsung or LG simulator (batch.md row).
3. **Roku's sign-in rule** (step A5): ask Roku first and tell me what it says.

## Record what happened

When a step is done, write the date and any store messages here (or send them to me): account created, package uploaded, submitted, accepted or what the store asked to change. Stores' own review wording is useful for a fix brief.

| Step | Done on | Notes |
|---|---|---|
| Step 0, sizes compared | | |
| A1 to A3 Roku account, sideload, signed package | | |
| A5 Roku's sign-in answer | | |
| A4 Roku channel submitted | | |
| B1 to B2 Samsung account, certificate, signed .wgt | | |
| B3 Samsung submitted | | |
| C1 to C2 LG account, .ipk built | | |
| C3 LG submitted | | |
