# Player controls v1

The Dashboard's player is the reference for every Tuvima client: the order of
the controls, their names, the defaults, the panels and what the lock screen
shows are written here once, and each client (Roku, Android TV, Apple TV, the
TV web app, CarPlay, Android Auto and the phone apps) reads this file instead of
inventing its own. The words are the words the Dashboard shows. This file sits
beside the API contract in `README.md`; it says what the person sees and which
buttons work, not how a client talks to the server.

A contracts test (`PlayerControlsContractTests`) reads the tables below that are
marked with an HTML comment and compares them with what the Dashboard builds. If
the Dashboard changes an order, a name or a default, the test fails and names
the row, and this file changes in the same pull request.

Rules that hold everywhere:

- A control that cannot act is **dimmed in place**, never hidden, so the
  controls never move. A tool the media or the app does not support is **absent**
  and its space stays empty; nothing is a dead button.
- Names are user-facing: Library, Series, Universe, Work, Edition. A TV show is a
  show and a book sequence is a series.
- Text is US English. Where a string carries a number, `{0}` and `{1}` mark where
  it goes.
- Everything in the tables is for the **bar** of the Dashboard's player. A client
  with another screen (a television, a car, a phone) keeps the order, names and
  defaults and changes only the layout, as the later sections say.

## Transport

The five buttons around Play, in this order, for every state of the player.
Previous and Next mean a track (music), a chapter (audiobooks), an episode (TV)
or a queue item (a film, a folder video or a lesson, when a playlist or queue is
playing). When there is nothing before or after, they are dimmed in place.

**Previous in music restarts a song.** After 3 seconds of play it restarts the
current song. Within the first 3 seconds, or when it is pressed again within 3
seconds of a restart, it goes to the previous song. At the start of the queue it
restarts. The 3 seconds is `music_previous_restart_seconds` in the Dashboard's
`config/ui/playback-client.json`. Songs have no skip-by-seconds buttons.

Repeat has three states: Repeat off, Repeat all and Repeat one (Repeat all shows
a ring, Repeat one a ring with "1"). Shuffle and Repeat are selected (purple)
when on. Play reads "Pause" while playing.

Music:

<!-- contract-table: transport-music -->
| Order | Control | Label | Dimmed in place |
| --- | --- | --- | --- |
| 1 | shuffle | Shuffle | no |
| 2 | previous | Previous track | yes |
| 3 | play-pause | Play | no |
| 4 | next | Next track | yes |
| 5 | repeat | Repeat | no |

Audiobooks:

<!-- contract-table: transport-audiobook -->
| Order | Control | Label | Dimmed in place |
| --- | --- | --- | --- |
| 1 | previous | Previous chapter | yes |
| 2 | skip-back | Skip back 10 s | no |
| 3 | play-pause | Play | no |
| 4 | skip-forward | Skip forward 30 s | no |
| 5 | next | Next chapter | yes |

TV episodes:

<!-- contract-table: transport-tv -->
| Order | Control | Label | Dimmed in place |
| --- | --- | --- | --- |
| 1 | previous | Previous episode | yes |
| 2 | skip-back | Skip back 10 s | no |
| 3 | play-pause | Play | no |
| 4 | skip-forward | Skip forward 30 s | no |
| 5 | next | Next episode | yes |

Films, folder videos and lessons:

<!-- contract-table: transport-film -->
| Order | Control | Label | Dimmed in place |
| --- | --- | --- | --- |
| 1 | previous | Previous | yes |
| 2 | skip-back | Skip back 10 s | no |
| 3 | play-pause | Play | no |
| 4 | skip-forward | Skip forward 30 s | no |
| 5 | next | Next | yes |

## Skip and restart defaults

Skip back is 10 seconds and skip forward is 30 seconds for every media that has
them. A profile can change them in Playback settings; the choices differ a little
between video and audio because they are two saved pairs. A client reads the
profile's saved pair and uses these numbers when nothing is saved.

<!-- contract-table: skip-defaults -->
| Setting | Default | Video choices | Audio choices |
| --- | --- | --- | --- |
| Skip back | 10 | 5, 10, 15, 30 | 10, 15, 30 |
| Skip forward | 30 | 10, 30, 60, 90 | 15, 30, 60 |
| Previous restarts a song after | 3 | none | none |

The last row is in seconds and applies to music only.

## Tools

The tools sit to the right of the bar, in this order. A tool shows only when the
media and the app support it; the "Shown when" column says what that is.

- `always`: the tool is always there.
- `has queue`: there is a queue; `has chapters`: the book has timed chapters;
  `has episodes`: the show or the folder lists other videos; `can bookmark`: the
  item supports bookmarks; `can fullscreen`: the screen can go full screen.

Music has no Sleep timer tool. Audiobooks show the current speed on the Speed
chip ("1.25×"). Now playing is an icon button and is selected while that page is
open. Close is the only action that stops and dismisses the player.

Music:

<!-- contract-table: tools-music -->
| Order | Control | Label | Shown when |
| --- | --- | --- | --- |
| 1 | lyrics | Lyrics | always |
| 2 | queue | Queue | has queue |
| 3 | bookmarks | Bookmarks | can bookmark |
| 4 | volume | Volume | always |
| 5 | now-playing | Now playing | always |
| 6 | close | Close | always |

Audiobooks:

<!-- contract-table: tools-audiobook -->
| Order | Control | Label | Shown when |
| --- | --- | --- | --- |
| 1 | chapters | Chapters | has chapters |
| 2 | speed | Playback speed | always |
| 3 | sleep-timer | Sleep timer | always |
| 4 | bookmarks | Bookmarks | can bookmark |
| 5 | volume | Volume | always |
| 6 | now-playing | Now playing | always |
| 7 | close | Close | always |

TV episodes:

<!-- contract-table: tools-tv -->
| Order | Control | Label | Shown when |
| --- | --- | --- | --- |
| 1 | episodes | Episodes | has episodes |
| 2 | audio-subtitles | Audio & subtitles | always |
| 3 | bookmarks | Bookmarks | can bookmark |
| 4 | volume | Volume | always |
| 5 | playback-settings | Playback settings | always |
| 6 | fullscreen | Fullscreen | can fullscreen |
| 7 | close | Close | always |

Films:

<!-- contract-table: tools-film -->
| Order | Control | Label | Shown when |
| --- | --- | --- | --- |
| 1 | audio-subtitles | Audio & subtitles | always |
| 2 | bookmarks | Bookmarks | can bookmark |
| 3 | volume | Volume | always |
| 4 | playback-settings | Playback settings | always |
| 5 | fullscreen | Fullscreen | can fullscreen |
| 6 | close | Close | always |

Folder videos and lessons have a film's controls and add the list of the other
videos when there are any. The tool and its panel are both named Lessons:

<!-- contract-table: tools-lesson -->
| Order | Control | Label | Shown when |
| --- | --- | --- | --- |
| 1 | episodes | Lessons | has episodes |
| 2 | audio-subtitles | Audio & subtitles | always |
| 3 | bookmarks | Bookmarks | can bookmark |
| 4 | volume | Volume | always |
| 5 | playback-settings | Playback settings | always |
| 6 | fullscreen | Fullscreen | can fullscreen |
| 7 | close | Close | always |

## Phone

On a phone (a phone form factor, or a window 720 px wide or narrower) the bar
becomes a small collapsed bar and the player grows into a full-screen Now
playing view that a tap on the bar opens. The Dashboard decides "phone" once
(`PlaybackFormFactor`), so the music, audiobook and video players always agree.
Order, names and defaults are the ones above; only the layout changes.

The collapsed bar is 56 px high: the art, the title and the second line, then
these buttons. It has no tools and no Favorite.

<!-- contract-table: phone-bar -->
| Media | Buttons |
| --- | --- |
| Music | play-pause: Play, next: Next track |
| Audiobook | play-pause: Play, skip-forward: Skip forward 30 s |
| Video | play-pause: Play |

The expanded player for music and audiobooks has a top line (collapse, "Playing
from ...", the "..." menu), the artwork, the title line with Favorite (and Like
and Dislike for music), the timeline, these transport buttons with a large Play,
and then one row of at most four tools. There is no volume control: the phone's
own buttons set the volume. Music Bookmarks, Playback settings and the item
actions are in the "..." menu.

<!-- contract-table: phone-transport -->
| Media | Buttons |
| --- | --- |
| Music | previous: Previous track, play-pause: Play, next: Next track |
| Audiobook | previous: Previous chapter, skip-back: Skip back 10 s, play-pause: Play, skip-forward: Skip forward 30 s, next: Next chapter |

Video on a phone keeps its overlay with the five transport buttons of the TV
episode and film tables above, and its own tool row. The tool row of every phone
player, with every "Shown when" condition on:

<!-- contract-table: phone-tools -->
| Media | Tools |
| --- | --- |
| Music | shuffle: Shuffle, lyrics: Lyrics, queue: Queue, repeat: Repeat |
| Audiobook | chapters: Chapters, speed: Playback speed, sleep-timer: Sleep timer, bookmarks: Bookmarks |
| Audiobook without chapters | speed: Playback speed, sleep-timer: Sleep timer, bookmarks: Bookmarks |
| TV episode | audio-subtitles: Audio & subtitles, episodes: Episodes, bookmarks: Bookmarks, fullscreen: Fullscreen |
| Film | audio-subtitles: Audio & subtitles, bookmarks: Bookmarks, fullscreen: Fullscreen |
| Folder video or lesson | audio-subtitles: Audio & subtitles, episodes: Lessons, bookmarks: Bookmarks, fullscreen: Fullscreen |

Panels are bottom sheets on a phone and on a touch tablet up to 1024 px wide;
nothing depends on hover, and the name of an icon-only button shows on a long
press.

## Panels

Each tool opens one panel. Only one panel is open at a time; opening another
closes the first, and closing a panel never changes playback. The header (the
panel's name, a back arrow in a nested view, Close) and the footer are fixed, and
only the middle scrolls. The "Contents" column lists the fixed words of the
panel's heading, rows and states; the "Footer" column lists the fixed footer.
Words with `{0}` are filled in. Items are separated by semicolons.

<!-- contract-table: panels -->
| Panel | Media | Opened from | Contents | Footer |
| --- | --- | --- | --- | --- |
| Queue | Music | Queue | Up next · {0}; History; Clear; Play next; Move up; Move down; Remove; Go to album; Go to artist | Playing from |
| Episodes | TV episodes | Episodes | Season; {0} episodes; Up next; Playing; Paused | Autoplay next episode |
| Lessons | Folder videos and lessons | Lessons | {0} lessons; Up next; Playing; Paused | Autoplay next episode |
| Chapters | Audiobooks | Chapters | Chapter {0} of {1}; Book · {0}% complete; {0} elapsed · {1} remaining; Finished | Timeline shows; Chapter; Whole book; Select a chapter to start listening. |
| Lyrics | Music | Lyrics | Synced; Unsynced; Open Now playing; Resume following; Select a line to jump to that moment.; Looking for lyrics…; Find lyrics; Add a lyrics file | Synced lyrics · {0}; Unsynced lyrics · {0}; Local file; LRCLIB; Find another; About lyrics sources |
| Audio & subtitles | TV episodes, films, folder videos and lessons | Audio & subtitles | Audio; Subtitles; Director commentary; Audio description; {0} · SDH; {0} · Forced; Not switchable in this browser | Subtitle appearance; Find subtitles… |
| Subtitle appearance | The same video media | Audio & subtitles footer | Style; Default; Large text; High contrast; Text size; Small; Medium; Large; Background; None; Shadow; Box; Custom appearance; Font, colors and opacity | Reset to default; Changes saved automatically |
| Playback speed | Audiobooks (the Speed chip); other media through Playback settings | Playback speed | Current speed | Reset to 1× |
| Sleep timer | Audiobooks | Sleep timer | Stopping in; Playback will pause automatically.; Stopping at the end of chapter {0}; End of chapter; Custom duration… | +5 minutes; Cancel timer |
| Playback settings | TV episodes, films, folder videos and lessons (the gear); music and audiobooks (the "..." menu) | Playback settings | Playback speed; Skip back; Skip forward; Picture-in-picture; Playback information | Reset playback preferences |
| Bookmarks | Every media that has bookmarks | Bookmarks | Bookmark at {0}; Add a note…; Global · {0}; Mine · {0}; Search bookmarks; Edit; Delete; Keep; Name; Note; Save; Cancel; Couldn’t save · Try again; No Global bookmarks on this title yet; No bookmarks of yours yet; No bookmarks match your search; Select a bookmark to jump to that moment. | Show on timeline |

Notes on single panels:

- **Queue**: two tabs, "Up next · {0}" and "History". Up next has a Now playing
  row, then the upcoming rows with a "..." menu each. Clear removes the upcoming
  items and never stops the current song. The footer says where the queue is
  playing from, followed by the source's name.
- **Episodes and Lessons**: a Season dropdown (Episodes only) and the count
  ("20 episodes"); a row per item with its still, number and title, length and a
  one-line synopsis; the current row reads "Playing · 2:23 of 22:41" (or
  "Paused"); the next item has an "Up next" chip. Selecting a row plays or
  resumes it. The Autoplay switch is the profile's saved preference.
- **Chapters**: the identity row, "Chapter 8 of 24" and "Book · 32% complete";
  a check on finished chapters; the current row reads "12:08 elapsed · 18:32
  remaining". The footer's Chapter or Whole book choice decides what the
  timeline shows and is saved per profile.
- **Lyrics**: a "Synced" or "Unsynced" chip. Synced lyrics follow playback, a
  manual scroll suspends following and shows a "Resume following" pill, and
  selecting a timed line seeks. Unsynced lyrics are plain text with no seeking.
- **Audio & subtitles**: audio rows show the language and the track facts
  ("Dolby Atmos · 7.1"), with commentary and audio-description tracks last;
  subtitle rows start with Off. A selected row has a check. A track the app
  cannot switch is listed as not selectable with "Not switchable in this
  browser". Changing either keeps the position within a second.
- **Playback speed**: presets 0.75×, 1×, 1.25×, 1.5× and 2×, plus a slider from
  0.5× to 3× in steps of 0.05; a preset is ticked only when the speed equals it.
- **Playback settings**: a row per setting; a row appears only when the media
  and the app support it (music has neither speed nor skips).
- **Bookmarks** (every word is in the table above, which the tests check): the
  header "Bookmarks"; a full-width button "Bookmark at 34:12" with a plus
  that saves at the time it was pressed, at once, with no note; an "Add a note…" field; a two-way choice
  **Mine | Global**, Mine by default (the choice shows only when a Global set exists, on movies and TV). Global is the set an administrator made for the title; each row shows its maker's name. Mine is the profile's own and
  lets the person Edit (rename and note) and Delete. Rows show the time, the
  title or note, and a frame from that moment (video) or the cover (audio);
  selecting a row seeks. A search box appears above eight rows. Empty states are
  "No Global bookmarks on this title yet" (Global) and "No bookmarks of yours yet"
  (Mine). There is no copy, share, import or save-to-mine action in the player.
  The Engine decides what a profile may see; a client never filters by its own
  profile.

### Sleep timer presets

Four presets, then End of chapter (audiobooks with chapters) and a custom length.
While a timer runs the panel leads with "Stopping in 14:32" and "Playback will
pause automatically." (an end-of-chapter timer says "Stopping at the end of
chapter 8"), and the footer shows "+5 minutes" and "Cancel timer". "+5 minutes"
sets a new timer at what is left plus five minutes.

<!-- contract-table: sleep-timer-presets -->
| Choice | Label |
| --- | --- |
| 15 | 15 min |
| 30 | 30 min |
| 45 | 45 min |
| 60 | 60 min |
| end-of-chapter | End of chapter |
| custom | Custom duration… |

### Speed presets

<!-- contract-table: speed-presets -->
| Preset | Label |
| --- | --- |
| 0.75 | 0.75× |
| 1 | 1× |
| 1.25 | 1.25× |
| 1.5 | 1.5× |
| 2 | 2× |

## Paused details and end card

**Paused details.** While a video is paused its details sit at the bottom left,
above the bar, on a local gradient (the whole picture is not dimmed). They hide
while a panel is open and come back when it closes. Rows, top to bottom:

1. the title (the show, the course or the film);
2. one line: "S1 E1 · The First Wish · 22 min" for an episode, "3 · Ball handling ·
   12 min" for a folder video, "2019 · 1h 52m" for a film (a length under an
   hour reads "22 min", from an hour "1h 52m");
3. a short description (episodes and lessons; a film has none here).

The rating card stays at the top left.

**End card.** As an episode or lesson ends, a card shows above the bar, never over
it, and closes when the next item starts. Rows, top to bottom: the heading
"Up next" and, when autoplay is on, the countdown "Starts in {0} seconds"; the
next item's still and title with "Next episode" or "Next lesson" under it; for a
course only, a "Homework for this lesson" block listing the supporting files marked
"Shown alongside" the lesson, each with an Open button; the buttons Cancel and Play now. With autoplay off the
card stays and has no countdown.

<!-- contract-table: end-card -->
| Row | Words |
| --- | --- |
| Heading | Up next |
| Countdown | Starts in {0} seconds |
| Kind (TV) | Next episode |
| Kind (lesson) | Next lesson |
| Homework | Homework for this lesson; Open |
| Buttons | Cancel; Play now |

## Lock screen and system controls

The operating system draws the lock screen, the notification shade and the media
overlay; a client supplies what goes on the card and which buttons work.
Headsets, car stereos and keyboards send the same commands.

- **Line 1** is the title: the song, the book, the episode title, the film.
- **Line 2** is the same second line as the bar: artist · album; author · chapter
  name; show · S1 E1; year · Movie.
- **Artwork**: the square cover for music and books, the landscape still or poster
  for video, at 512 and 1024 pixels.
- **Position, duration and speed** are sent every second.
- **Skips** use the profile's skip seconds.
- A hidden episode title (the Spoilers setting) is never on the second line.
- A **Vault** item sends no metadata at all while Vault is locked: the card shows
  nothing rather than the title.

The actions per media use the system's own action names:

<!-- contract-table: lock-screen -->
| Media | Line 1 | Line 2 | Actions |
| --- | --- | --- | --- |
| Music | Stay a Little Longer | Maya Ellis · Northern Lights | play, pause, previoustrack, nexttrack, seekto |
| Audiobook | The Way Beyond | Maya Ellis · Chapter 8 | play, pause, seekbackward, seekforward, previoustrack, nexttrack, seekto |
| TV episode with a next episode | The First Wish | Shimmer and Shine · S1 E1 | play, pause, seekbackward, seekforward, seekto, nexttrack |
| TV episode, the last one | The First Wish | Shimmer and Shine · S1 E1 | play, pause, seekbackward, seekforward, seekto |
| Film | The Glass Harbor | 2026 · Movie | play, pause, seekbackward, seekforward, seekto |

Line 1 and line 2 above are examples built from a song by Maya Ellis on the album
Northern Lights, the audiobook "The Way Beyond" read from chapter "Chapter 8", the
episode "The First Wish" of "Shimmer and Shine" and the 2026 film "The Glass
Harbor". Previous in music follows the restart rule under Transport.

## Televisions

**First definition, to be settled in its own thread.** The key map, the overlay
rules and the side panels below are the starting definition for Roku, Android
TV, Apple TV and the TV web app; each is fine-tuned later.

Video fills the screen. The controls overlay appears on any key and hides after
5 seconds with no input, and never while a panel is open. Focus starts on the
transport row, which is the five buttons of the film or TV table above in that
order, with Previous and Next dimmed in place. Below it is one tool row in the
Dashboard's order, without Volume (the remote owns it) and without Fullscreen and
Close (a television is already full screen):

- TV episodes: Episodes, Audio & subtitles, Bookmarks, Playback settings;
- films: Audio & subtitles, Bookmarks, Playback settings;
- folder videos and lessons: Lessons, Audio & subtitles, Bookmarks, Playback
  settings (Lessons only when other videos follow).

A tool opens a **side panel on the right** with the same name, rows and footer as
the Panels table. The paused details, the end card, the Everyone | Mine
bookmarks and the Autoplay next episode switch are the same as above.

<!-- contract-table: tv-keys -->
| Key | Action |
| --- | --- |
| Any key | Show the overlay |
| Left and Right with the timeline focused | Skip back and skip forward by the profile's seconds (10 and 30 by default) |
| Left and Right elsewhere | Move focus |
| Up and Down | Move focus between the timeline, the transport row and the tool row |
| Select | Press the focused control |
| Play/Pause | Play or pause |
| Rewind | Skip back (the same action as the Skip back button) |
| Fast-forward | Skip forward (the same action as the Skip forward button) |
| Back | Close the open side panel first, then hide the overlay |

Music and audiobooks on a television are a full-screen Now playing: the cover,
the title and second line, the timeline and the same transport as the music or
audiobook table above, with Lyrics or Chapters as a side panel.

## Cars

**First definition, to be settled in its own thread.** CarPlay and Android Auto
draw their own interface from templates and allow only lists and a Now playing
card, so Tuvima supplies two things. Search is by voice through the car's
assistant. Nothing visual and no video appears.

**The list**, in this order:

<!-- contract-table: car-list -->
| Order | Entry |
| --- | --- |
| 1 | Continue |
| 2 | The Listen shelves |
| 3 | Audiobooks |
| 4 | Playlists |
| 5 | Recently played |

**The Now playing card** carries the fields of the lock screen above. Its
buttons:

- music: Previous, Play, Next, with Shuffle and Repeat as the template's extra
  buttons;
- audiobooks: back 10 seconds, forward 30 seconds (the profile's skip seconds),
  Previous chapter and Next chapter, with the Speed presets as the template's
  extra buttons.

## What each client does today

Each client's README has a Player section that points here and lists its gaps:
`clients/roku/README.md`, `clients/android/README.md` (Android TV and Android
Auto), `clients/apple/README.md` (Apple TV and CarPlay) and
`clients/tv-web/README.md`.
