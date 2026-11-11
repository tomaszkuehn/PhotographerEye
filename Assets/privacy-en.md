# PhotographerEye Privacy Policy

Version of 11 November 2026

## 1. Who is responsible

PhotographerEye is written by Tomasz Kuehn (contact: tomasz.kuehn@gmail.com). The app runs entirely on your computer. The author runs no server for it and receives no data from it.

## 2. What the app does not do

- It sends no telemetry, usage statistics or crash reports.
- It has no user accounts, no ads and no tracking.
- It sends your photos and data nowhere, except through the AI assistant, and only when you use it (section 4).

## 3. Data stored on your computer

The app keeps data only on your computer. The author has no access to it, and you can delete it at any time.

- **Settings**, in `%APPDATA%\PhotographerEye\settings.json`. This file holds view options, recently opened folders and the AI assistant settings. It also holds the last 50 requests sent to the assistant and the date you accepted this policy. It also contains your Ollama API key, encrypted with Windows DPAPI so that only your Windows account can read it.
- **Plugins, presets, LUTs and plugin data**, in `%APPDATA%\PhotographerEye`.
- **Remembered previews**, in `%LOCALAPPDATA%\PhotographerEye\previews`. Switch them off with *Remember previews* and delete them with *Clear preview cache*.
- **.eye files**, in the `_eye_` subfolder next to your photos. They hold edit settings, masks and the AI assistant conversation: requests, answers, change summaries and token counters. They hold no photo previews. *New conversation* removes the conversation, and you can delete the whole file.
- **Temporary files** in `%TEMP%`, created while decoding RAW photos and while installing plugins.
- **A diagnostic log**, `%TEMP%\photographereye-debug.log`. It is written only when you set the environment variable `PHOTOGRAPHEREYE_DEBUG=1`.

## 4. AI assistant (optional)

The assistant connects to a network only when you open its window, to read the list of models, and when you send a request. It connects to the Ollama server chosen in the assistant settings:

- **Local**, `http://localhost:11434` by default. Data does not leave your computer. The exception is models ending in `-cloud`: the local Ollama passes the data on to ollama.com.
- **Ollama Cloud** (`https://ollama.com`), with your API key. The key is sent only to that server.

The model receives:

- Your requests and the earlier conversation about this photo.
- The current edit settings, active plugins, crop and masks.
- Image statistics, such as the histogram and brightness, and the photo's dimensions.
- A JPEG preview of the photo, 1024 px on the long side. It goes only to models that can see images.
- Camera data: model, ISO, aperture, shutter speed, focal length and date. It is sent only when you switch this option on.

Never sent: GPS location, the file path or any other photos.

Before data is first sent to the cloud, the app asks for your consent. Data sent to ollama.com is processed by Ollama under its own privacy policy. The author of PhotographerEye does not receive it.

## 5. Plugins

The plugins bundled with the app work locally and do not connect to any network. Plugins by other authors that you install yourself run with the app's permissions. Their authors are responsible for what they do, so install plugins only from sources you trust.

## 6. Links

The links in the *About* window (e-mail, GitHub) open in your browser or mail program. This happens only when you click them.

## 7. Your rights

The author collects no data about you, so there is nothing on the author's side to provide, correct or delete. All the data described in section 3 is under your control. Send privacy questions to the address in section 1.

## 8. Changes

When this policy changes, the new version is shown when the app starts and must be accepted again. The current version and the date you accepted it are in the settings, under *Privacy policy*.
