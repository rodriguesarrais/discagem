# Client packages served by the server

The server offers the desktop client in this folder for download (`/download`) and as an update to
installed clients (`/api/client/latest`). The Release workflow puts the release's Windows installer,
.deb and Flatpak here before building the server image; in a checkout the folder holds only this file,
and a server built from it offers no downloads (clients then check GitHub for updates).

On a server installed without the image, point `Discagem__ClientDownloadsDirectory` at a folder
holding `Discagem-<version>-win-x64-setup.exe`, `discagem_<version>_amd64.deb` and/or
`Discagem-<version>-x86_64.flatpak` from a release.
