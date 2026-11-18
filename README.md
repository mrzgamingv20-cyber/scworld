# ArenaCS + TdmCS

Dua mod Survivalcraft (API 1.9) dalam satu repo, dibuild bersamaan oleh `.github/workflows/build.yml`.

- `ArenaCS/` : membangun arena CS 64x64 (map). Pengaturan -> "Bangun Arena CS" = Ya, lalu masuk dunia datar.
- `TdmCS/`   : Team Deathmatch (tim, skor, uang, toko senjata, respawn, bot). Butuh mod CS (Knives + Tactical).
- `libs/`    : taruh `ScCsgoKnives.dll` dan `ScCsgoTactical.dll` di sini.

Hasil build: Actions -> run terbaru -> Artifacts -> `ArenaCS-scmod`, `TdmCS-scmod`.
Belum dikompilasi/diuji di game; kirim log kalau ada error.
