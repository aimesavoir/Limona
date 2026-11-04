Limona - bundled fonts
===========================

These files are installed per-user (no admin) the first time Limona.exe
runs: the app copies them to
  %LOCALAPPDATA%\Microsoft\Windows\Fonts
and registers them under
  HKCU\Software\Microsoft\Windows\CurrentVersion\Fonts

One-file installer
------------------
Run:  Limona.exe --install-fonts
This installs/registers the pack and exits immediately. Setup can call it.

Bundled families (all SIL Open Font License 1.1, see OFL.txt)
------------------------------------------------------------
  Cairo-Regular.ttf / Cairo-Bold.ttf                 (Cairo)
  Tajawal-Regular.ttf / Tajawal-Bold.ttf             (Tajawal)
  NotoSansArabic-Regular.ttf / NotoSansArabic-Bold.ttf (Noto Sans Arabic)
  BebasNeue-Regular.ttf                              (Bebas Neue)
  Almarai-Regular.ttf / Almarai-Bold.ttf             (Almarai)
  ArefRuqaa-Regular.ttf / ArefRuqaa-Bold.ttf         (Aref Ruqaa)
  CascadiaCode.ttf                                   (Cascadia Code)

Fonts the app can use if they are installed on the machine
-----------------------------------------------------------
  Agency, Ambawang Signature, Amerika Signature, AR Techni,
  Bahnschrift Condensed, Book Signature, Khaled Font, DigifaceWide,
  Trade Gothic Inline, (A) Arslan Wessam A, Ammar Thin,
  Ara Alharbi Alhanoof

Those are commercial or free-for-personal-use fonts and cannot be
redistributed with the app. If you own a license, drop the .ttf/.otf files
into this folder: they are installed per-user on the next launch. To make a
dropped font appear in the text font menu, add its family name to
PreferredFonts in Menu\ImageEditWindow.cs.

Adding more fonts
-----------------
Any .ttf/.otf placed here is installed per-user automatically (its display
name is read from the font's own name table). Only families listed in
PreferredFonts appear in the editor menu.
