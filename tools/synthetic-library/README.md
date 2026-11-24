# Large synthetic library generator

Writes about 2,700 small placeholder files (about 460 MB) that look like a real, messy home library: 343 books
(30 Series with novellas, other languages, second editions), 214 comics, 45 audiobooks, 155 films (franchises,
several editions of one film, remakes that share a title), 24 shows (567 episodes, specials, a combined-episode
file, a show numbered without seasons), 65 albums (900 songs, multi-disc, compilations, untagged), folders of
videos, personal videos and 330 photos, plus stray, empty and corrupt files. No real media is included.

```
pip install Pillow
python tools/synthetic-library/Generate-SyntheticLibrary.py <new-empty-folder>/library --no-network
pwsh -File tools/Run-IngestionBaseline.ps1 -SampleRoot <new-empty-folder>/library -Label synth-large -BudgetMinutes 360
```

`ffmpeg` must be on PATH, or set the `FFMPEG` environment variable to its full path. The file list, with the hard
case each file tests, is written to `synthetic-manifest.json` beside the library folder. The ingestion run is the
normal isolated baseline run: its own data store and ports, and it only reads the generated folder.
