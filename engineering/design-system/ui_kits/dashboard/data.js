// Dashboard UI kit — data fixtures.
// Evocative Tuvima-shaped entries, no emoji.

window.TUVIMA_DATA = {
  hero: [
    {
      id: 'dune',
      eyebrow: 'Continue Your Journey',
      title: 'Dune',
      sub: 'You\u2019ve read 38% of Frank Herbert\u2019s novel. Scott Brick\u2019s audiobook picks up where you left off \u2014 and the Villeneuve film waits in the same collection.',
      dominant: '#8B5A2B',
      bg: 'linear-gradient(125deg, #C28B4A 0%, #6B3F1A 40%, #1F0E04 95%)',
      coverText: 'Dune',
      progress: 38,
      cta: 'Continue Reading',
    },
    {
      id: 'gods',
      eyebrow: 'Recently Added',
      title: 'American Gods',
      sub: 'Neil Gaiman\u2019s full-cast audiobook landed in your media library 3 days ago. Bill Nighy narrates Mr. Wednesday.',
      dominant: '#0F2A4A',
      bg: 'linear-gradient(135deg, #1F4B7A 0%, #0F2A4A 55%, #0A1020 100%)',
      coverText: 'American\nGods',
      progress: 0,
      cta: 'Start Listening',
    },
    {
      id: 'peaky',
      eyebrow: 'Pick up where you left off',
      title: 'Peaky Blinders',
      sub: 'Season 4, Episode 3. The BBC drama sits alongside its soundtrack and the companion novels in one collection.',
      dominant: '#4B1C2C',
      bg: 'linear-gradient(135deg, #7A2A3F 0%, #4B1C2C 45%, #160610 100%)',
      coverText: 'Peaky\nBlinders',
      progress: 62,
      cta: 'Resume Watching',
    },
  ],
  continue: [
    { id:'dune', title:'Dune', meta:'Frank Herbert \u00b7 38%', bg:'linear-gradient(160deg,#C28B4A,#1F0E04)', coverText:'Dune', progress:38, types:['book','audio','movie'] },
    { id:'peaky', title:'Peaky Blinders', meta:'S4 \u00b7 E3', bg:'linear-gradient(160deg,#7A2A3F,#160610)', coverText:'Peaky\nBlinders', progress:62, types:['tv'] },
    { id:'expanse', title:'Leviathan Wakes', meta:'James S. A. Corey \u00b7 71%', bg:'linear-gradient(160deg,#1A3A4C,#050F18)', coverText:'Leviathan\nWakes', progress:71, types:['book','audio'] },
    { id:'killers', title:'Killers of the Flower Moon', meta:'David Grann \u00b7 12%', bg:'linear-gradient(160deg,#5A3420,#1A0A04)', coverText:'Flower\nMoon', progress:12, types:['book','movie'] },
    { id:'shogun', title:'Sh\u014dgun', meta:'Clavell \u00b7 E7', bg:'linear-gradient(160deg,#4B1820,#0E0308)', coverText:'Sh\u014dgun', progress:45, types:['book','tv'] },
    { id:'pines', title:'Under the Whispering Door', meta:'TJ Klune \u00b7 89%', bg:'linear-gradient(160deg,#2E4A3A,#0A1A12)', coverText:'Whispering\nDoor', progress:89, types:['book'] },
  ],
  recent: [
    { id:'gods', title:'American Gods', meta:'Neil Gaiman \u00b7 2019', bg:'linear-gradient(155deg,#1F4B7A,#0A1020)', coverText:'American\nGods', ribbon:'New', types:['audio'] },
    { id:'arcane', title:'Arcane', meta:'Netflix \u00b7 2 seasons', bg:'linear-gradient(155deg,#5A2A6B,#180A22)', coverText:'Arcane', ribbon:'New', types:['tv'] },
    { id:'miyazaki', title:'The Boy and the Heron', meta:'Studio Ghibli \u00b7 2023', bg:'linear-gradient(155deg,#2E4A3A,#0A1A12)', coverText:'The Boy\n& the Heron', ribbon:'New', types:['movie'] },
    { id:'dragon', title:'Fourth Wing', meta:'Rebecca Yarros', bg:'linear-gradient(155deg,#4A1820,#1A0608)', coverText:'Fourth\nWing', ribbon:'New', types:['book','audio'] },
    { id:'brandon', title:'Mistborn: The Final Empire', meta:'Brandon Sanderson', bg:'linear-gradient(155deg,#2A2040,#0A0818)', coverText:'Mistborn', types:['book','audio'] },
    { id:'boygenius', title:'the record', meta:'boygenius \u00b7 Album', bg:'linear-gradient(155deg,#B07A3A,#3A1F08)', coverText:'the\nrecord', types:['music'] },
    { id:'doune', title:'Dune: Part Two', meta:'Villeneuve \u00b7 2024', bg:'linear-gradient(155deg,#B8804A,#2A1208)', coverText:'Dune\nPart Two', types:['movie'] },
  ],
  might_like: [
    { id:'tolkien', title:'The Silmarillion', meta:'J.R.R. Tolkien', bg:'linear-gradient(155deg,#2E3A4A,#0A1018)', coverText:'Silmarillion', types:['book'] },
    { id:'foundation', title:'Foundation', meta:'Apple TV+ \u00b7 2 seasons', bg:'linear-gradient(155deg,#4A3A1A,#1A1208)', coverText:'Foundation', types:['tv','book'] },
    { id:'becky', title:'A Memory Called Empire', meta:'Arkady Martine', bg:'linear-gradient(155deg,#2A1A4A,#080618)', coverText:'Memory\nCalled Empire', types:['book'] },
    { id:'tyler', title:'Call Me If You Get Lost', meta:'Tyler, The Creator', bg:'linear-gradient(155deg,#B85A20,#2A1004)', coverText:'Call Me\nIf You\nGet Lost', types:['music'] },
    { id:'fargo', title:'Fargo', meta:'FX \u00b7 5 seasons', bg:'linear-gradient(155deg,#8A4A2E,#1A0A04)', coverText:'Fargo', types:['tv'] },
    { id:'stormlight', title:'The Way of Kings', meta:'Brandon Sanderson', bg:'linear-gradient(155deg,#1A2A4A,#050A18)', coverText:'The Way\nof Kings', types:['book','audio'] },
  ],
  collection: {
    dune: {
      title: 'Dune',
      eyebrow: 'Universe \u00b7 Dune',
      byline: ['Frank Herbert', 'Denis Villeneuve', '1965 \u2192 2024'],
      dominant: '#8B5A2B',
      coverBg: 'linear-gradient(160deg, #C28B4A 0%, #6B3F1A 50%, #1F0E04 100%)',
      coverText: 'Dune',
      description: 'The complete Dune universe \u2014 Frank Herbert\u2019s six-book saga, Scott Brick\u2019s audiobooks, the Lynch and Villeneuve films, and the companion scores. Organized by story, not by file type.',
      editions: [
        { type:'Book',      title:'Dune (Hardcover)',           meta:'Frank Herbert \u00b7 Ace, 1965 \u00b7 EPUB',   icon:'book-open.svg', progress:38 },
        { type:'Audiobook', title:'Dune (Unabridged)',          meta:'Scott Brick, Simon Vance \u00b7 21h 02m',       icon:'headphones.svg' },
        { type:'Movie',     title:'Dune: Part One',             meta:'Denis Villeneuve \u00b7 2021 \u00b7 2h 35m',    icon:'film.svg' },
        { type:'Movie',     title:'Dune: Part Two',             meta:'Denis Villeneuve \u00b7 2024 \u00b7 2h 46m',    icon:'film.svg' },
        { type:'Book',      title:'Dune Messiah',               meta:'Frank Herbert \u00b7 Ace, 1969 \u00b7 EPUB',    icon:'book-open.svg' },
        { type:'Music',     title:'Dune (Original Soundtrack)', meta:'Hans Zimmer \u00b7 2021 \u00b7 22 tracks',      icon:'music.svg' },
      ],
    }
  }
};

