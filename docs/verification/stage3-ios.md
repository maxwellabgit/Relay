# Stage 3 — iPhone / TestFlight

RELAY reuses the nepaliTranslation Expo/EAS workflow with **new** identifiers.

## Do not copy from NepTranslate

* Bundle ID
* EAS project ID
* App Store ID
* Runtime secrets / TypeSafe keys

## Commands

```powershell
cd apps/relay
npm install
npx eas-cli login
npx eas build:configure
# Replace extra.eas.projectId in app.config.ts with the new RELAY project
npx eas build --platform ios --profile production
npx eas submit --platform ios --latest
```

## Runtime requirements

* Same `apps/relay` Expo UI and TypeScript engine
* `expo-sqlite` + shared migrations
* Keychain/SecureStore-backed TypeSafe transport (`adapters/expo`)
* Native audio + CoreBluetooth Halo transport in a development client / TestFlight build
* Expo Go is **not** a release gate

TypeSafe keys are entered by the user into Keychain for personal builds, or held behind an authenticated broker for multi-user products — never in `app.config.ts` or JS assets.
