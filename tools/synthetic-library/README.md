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

## Reading the results

```
python tools/synthetic-library/Analyze-Run.py <run-folder>/baseline-record.json <new-empty-folder>/synthetic-manifest.json --out report.md
```

The report compares what each file was meant to be with what the Engine made of it: what was picked up, match
results per media type, likely wrong matches, Series and Universe grouping, cuts and remakes, shows, albums, the
hard cases, Needs Review, photos, waiting times and outside requests.

## Provider keys, held files and looking at the results

- The run now points itself at this computer's own root key (read only), so the saved provider keys open. If they
  cannot be opened it says so by name, says which kinds of media it cannot identify (films and TV need TMDB or TheTVDB,
  comics need Comic Vine), lists the files it will therefore never register in the record under `held_kinds`, and ends
  with exit code 7. The analysis report starts with the same warning. Never commit or print the config copy.
- A fresh checkout works as it is: when `config/libraries.json` has no library for a type, the run makes an empty one.
  (The old `Prepare-ConfigSource.py` helper is gone.)
- To look at a finished run in the Dashboard, make a copy whose first-time setup starts again:
  `pwsh -File tools/Run-IngestionBaseline.ps1 -OpenStoreOf <run folder, id or label>`. It prints the two settings to start
  the Engine and Dashboard with; finish the wizard once. The run's own data is not touched.
- On Windows set `PYTHONUTF8=1` before running the Python scripts. Allow at least six hours for the full set with every source working.
