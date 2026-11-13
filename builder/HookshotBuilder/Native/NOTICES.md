# Motor de torrent (opção "Baixar da sua fonte")

`hydra_torrent_bridge.dll` é a ponte de libtorrent 2.1.1 do projeto
[Hydra](https://github.com/hydralauncher/hydra), sem modificação, copiada de
`native/torrent-bridge/prebuilt/x64-windows-static/bin`. `trackers.json` é a lista
de trackers de reserva do mesmo projeto. O Hydra é licenciado sob MIT
(`LICENSE-Hydra-MIT.txt`); a libtorrent e as demais dependências da DLL estão em
`THIRD_PARTY_NOTICES.txt`.

O Builder confere o SHA-256 da DLL antes de carregá-la (`SourceDownload.cs`).
