# Google Play submission: what Shy does by hand (phone app and TV app)

> Spec: [native-apps](../specs/native-apps.md), WP12 · Written 2026-11-23 · Text to paste: [native-android-play-listing-2026-11.md](native-android-play-listing-2026-11.md) · Status: Not started (needs Shy)

The cloud build made the signing setup, the version numbers, the release workflow and the text. Nothing below can be done by a build: Google needs a person to sign in, pay, accept agreements and press upload. I wrote this from what I knew of Google Play's process; I could not open Play Console from the build environment, so button names and numbers may differ (each step says what to check). Nothing is ever uploaded to Google Play by the workflow (D6 order: GitHub first, Play after phone parity).

## Cost and licence at a glance

- Google Play developer account: a one-time registration fee (I believe US $25; **check the amount on the sign-up page**). No yearly fee that I know of.
- Google keeps a share of every $4.99 sale (a service fee; **check the current rate** on Play's pricing page). The TV app is free.
- Tools you use on your own computer and that are not added to the repository: `keytool` (comes with the free Java JDK 17 that Library's Android build already needs), the `gh` command (GitHub's own), Android Studio's emulator (Apache 2.0). Nothing new goes into the repository.
- The apps are open source under MPL 2.0 (spec D1). The licence file travels in the source; Play shows the licence in the listing text.

## Step 0, decisions you owe before the first upload

1. **Personal or organisation account.** A personal account created after November 2023 must run a closed test with a minimum number of testers (I believe 12, opted in for 14 days in a row) before it may apply for production. An organisation account (with a D-U-N-S number for Tuvima) is exempt but needs company paperwork. D19 says "a few friends"; with a personal account that may not be enough people. Recommended: start with a personal account, read the number the console shows, and recruit up to it.
2. **Same signature on GitHub and Play?** Recommended: let Google create and keep the app signing key (the default, safest: if you lose your own key Google can reset your upload key). Then the APK on GitHub (signed with your upload key) and the app from Play (signed by Google) have different signatures, so a phone cannot update from one to the other without uninstalling first; the release notes already say so. The alternative (upload your own app signing key at enrolment so both match) works but means you are the only holder of the key that can never be replaced.
3. **How will Google's reviewer test it?** Same question as the TV stores: a reviewer cannot reach a server on a private network. Recommended: a small demo server on a public address with a test administrator, or video of a working pairing, whichever Play accepts.
4. **Data safety answers** (listing text, "Privacy and data safety answers"): confirm the recommended "no data collected" answer and the "not always encrypted" answer.
5. **A launcher icon for the phone app.** It has none yet. Ask me to add one (a small separate change that touches the phone app's manifest and resources) before the first bundle you upload; Play will accept the upload without it but the app would show a plain default icon on every phone.
6. **Support email** (a role address) and **category**.

## A. Make the upload key (about 15 minutes, once)

The upload key is the key you sign with; Play re-signs the app with its own key (Play App Signing). Keep the file and passwords out of the repository (the repository ignores `*.jks` as a safety net, but do not rely on it).

1. Pick a folder that is not inside any repository, for example `C:\Users\<you>\Secrets\library-android\`.
2. Run (Java 17's `keytool`; the command asks for the two passwords and your name and country, which end up in the key's certificate):

   ```
   keytool -genkeypair -v -keystore library-upload.jks -alias library-upload -keyalg RSA -keysize 4096 -validity 10000
   ```

   Use a long password you store in a password manager. If asked, the key password can be the same as the keystore password.
3. Back the file and both passwords up in two places (the password manager and an offline copy). Losing them is recoverable only through Play's "request upload key reset" form, which takes days.
4. Print the certificate fingerprint once and keep it with the passwords: `keytool -list -v -keystore library-upload.jks -alias library-upload`.

## B. Give GitHub the key (about 10 minutes, once; this is the "secrets from the CI store")

Repository, Settings, Secrets and variables, Actions, New repository secret. Four secrets with exactly these names:

| Secret | Value |
|---|---|
| `ANDROID_KEYSTORE_BASE64` | the keystore file as text: in PowerShell `[Convert]::ToBase64String([IO.File]::ReadAllBytes("C:\path\library-upload.jks"))` and paste the output |
| `ANDROID_KEYSTORE_PASSWORD` | the keystore password |
| `ANDROID_KEY_ALIAS` | `library-upload` |
| `ANDROID_KEY_PASSWORD` | the key password |

The `gh` command does the same from a terminal: `gh secret set ANDROID_KEYSTORE_PASSWORD` (it asks for the value, nothing lands in your shell history). Never paste a secret into a pull request, an issue or a chat.

## C. Try a signed build before tagging (about 20 minutes; this is Local check L1)

On your computer, with JDK 17, the Android SDK 37 and Gradle 9.3.1 installed, in a terminal (PowerShell shown) from `clients\android`:

```
$env:TUVIMA_ANDROID_KEYSTORE_FILE = "C:\path\library-upload.jks"
$env:TUVIMA_ANDROID_KEYSTORE_PASSWORD = Read-Host "Keystore password"
$env:TUVIMA_ANDROID_KEY_ALIAS = "library-upload"
$env:TUVIMA_ANDROID_KEY_PASSWORD = Read-Host "Key password"
gradle :mobile:assembleRelease :tv:assembleRelease
```

The signed files are in `mobile\build\outputs\apk\release\mobile-release.apk` and `tv\build\outputs\apk\release\tv-release.apk`. Without the four variables the same command still works and makes an unsigned APK, which no phone will install. Check a signature with `apksigner verify --print-certs <file>` (in the SDK's `build-tools` folder), then `adb install mobile-release.apk` onto an emulator or phone and open it. Close the terminal afterwards so the passwords leave memory.

## D. First release on GitHub (about 10 minutes)

1. Check `clients/android/version.properties` (0.1.0 for the first one) and that it is merged to `main`.
2. Tag and push: `git tag android-v0.1.0` then `git push origin android-v0.1.0`.
3. The "Android release" workflow builds the signed phone and TV APKs and bundles. It stops with a clear message if a secret is missing, the tag does not match the version file, or the tag is not on `main`.
4. When it is green, the GitHub Release holds the two APKs and `SHA256SUMS.txt`. The two `.aab` bundles are kept for 30 days under the workflow run's "Artifacts" (download them for step G).

## E. Create the Google Play developer account (about 30 minutes plus waiting)

1. Go to play.google.com/console, sign in with the Google account that will own the apps (use a work account for Tuvima, not a personal one that may change).
2. Choose the account type (Step 0, item 1), pay the fee, and finish the identity check Google asks for (a government ID, a phone number, an address; an organisation account also needs company details). Approval can take from hours to days.
3. Accept the Developer Distribution Agreement.
4. To sell the $4.99 phone app, set up the Google payments profile (Play Console, Setup, Payments profile): legal name, address, bank account for payouts and tax information. **Check** whether Google asks for anything else for your country.

## F. Create the two apps (about 20 minutes each)

For the phone app first, then the TV app (the TV app is free, so no payments step):

1. Create app, name `Library`, default language English (United States), App, and **Paid** for the phone app (this cannot be changed later), **Free** for the TV app. Accept the declarations.
2. Fill the store listing from [the listing text](native-android-play-listing-2026-11.md), including the privacy policy address and the pictures (the shot list says what to capture; none exist yet).
3. Set the price: $4.99 USD, then let Play convert to other countries (check the converted prices look sensible) and choose the countries.
4. App content section, one form at a time:
   - **Privacy policy:** the address in the listing text.
   - **App access:** the reviewer notes (Step 0, item 3).
   - **Ads:** No.
   - **Content rating:** the IARC questionnaire, answers in the listing text.
   - **Target audience and content:** general audience, not for children.
   - **Data safety:** the table in the listing text.
   - **Government, financial, health, news apps:** No to each.
   - **Advertising ID:** No (the app does not use it).
5. Phone app only: **Form factors.** The listing covers phone and tablet (D15). Leave Android Auto, Wear and TV unticked for now; Android Auto is declared later when the car list ships (spec WP11).
6. TV app only: it must show the TV banner and TV screenshots, and Play runs an extra TV review ("Optimized for TV"). Check the form factor list names Android TV.

## G. Upload to the closed test and add testers (about 30 minutes)

1. Testing, Closed testing, create a track (the first one is named "Alpha"; keep it), choose countries.
2. Create release, accept **Play App Signing** when asked (enrolment happens here: Google keeps the app signing key and your keystore becomes the upload key). Upload `Library-phone-0.1.0.aab` from the workflow's artifacts.
3. Release name `0.1.0`, release notes from the listing text ("What's new").
4. Testers: add a list (emails, or a Google Group) and save. Copy the opt-in link Play shows and send it to your friends; they open it on the phone, join, and install from Play.
5. Review and roll out. The first review can take days. Each later upload needs a higher versionCode: raise `versionBuild` in `version.properties`, merge, tag `android-v0.1.0-build1`, repeat Step D.
6. Read the closed-test rule shown in the console ("testing requirements"); if a minimum number of testers and days applies, wait and keep the test open.

## H. Public release (only after the test passes)

1. Apply for production access if the console asks (it asks the number of testers and a few questions about the test).
2. Promote the tested release to Production, countries and price as set, and send it for review.
3. Before this step, give the TV app its own test or release the same way (free).
4. Update `engineering/validation/batch.md` and the native-apps spec: tell me the dates and every message Google sent.

## Things that may go wrong

| What | Meaning | What to do |
|---|---|---|
| "Version code already used" | A bundle with that versionCode was uploaded before | Raise `versionBuild`, merge, tag with `-build<n>` |
| "You uploaded an unsigned or wrongly signed bundle" | The workflow was not used, or a different key signed it | Use only the bundles from the release workflow |
| The app is rejected for "app access" | The reviewer could not get past pairing | Add the demo server notes (Step 0, item 3) |
| Play says the app target version is too low | Google raised its minimum target API | Ask me; `targetSdk` is in `clients/android/*/build.gradle.kts` |
| Sideloaded APK blocked on some phones | Google has announced that installs from outside the store on certified phones will require the developer to be registered with Google (rolling out from late 2026) | **Check Google's "Android developer verification" page**; register the same developer account if it asks |
| Lost keystore or password | Play can reset the upload key, GitHub APKs cannot be updated over old ones | Use Play's "request upload key reset", then repeat Steps A and B with the new key |

## Decisions you owe, in one place

1. Account type (personal or organisation) and the testers' minimum.
2. Same signature on GitHub and Play, or not (recommended: not).
3. How Google's reviewer tests the app.
4. Data safety answers (recommended: no data collected; not always encrypted).
5. Whether to add a launcher icon before the first bundle.
6. Support email, category, and the Play app name if "Library" is taken.
