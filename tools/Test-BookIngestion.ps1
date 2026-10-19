<#
.SYNOPSIS
    Media Ingestion Test - drops synthetic EPUBs and M4Bs into the Tuvima watch folder
    and produces a before/after ingestion report.

.DESCRIPTION
    Generates synthetic EPUB and M4B files from a 110-title catalog (varying metadata
    quality) and drops a stratified selection into the watch folder. Monitors the
    engine API for completion, then queries results and writes a human-readable
    report. Tests both ebook and audiobook ingestion, including paired entries
    where the same book exists as both EPUB and M4B (validating the
    Books/{Title} - {QID}/Epub/ and Books/{Title} - {QID}/Audiobook/ layout).

    Scenarios:
      high      - Full metadata: title, author, year, ISBN, series, genre (60 books)
      audiobook - M4B format with iTunes metadata, narrator, ASIN (10 audiobooks)
      medium    - Partial: title, author, year only (20 books)
      low       - No embedded metadata; engine derives title from filename (10 books)
      corrupt   - Invalid file bytes; expect MediaFailed (5 books)
      duplicate - Identical bytes to an earlier book; expect DuplicateSkipped (5 books)

    Stratified sampling (default proportions):
      45% high, 15% audiobook, 20% medium, 10% low, 5% corrupt, 5% duplicate
      Foreign-language books capped at ~7% (max 2 per run).

.PARAMETER Count
    Number of books to select from the catalog using stratified sampling (default: 30, max: 110).

.PARAMETER Seed
    Random seed for reproducible runs. Omit for a fresh random selection.

.PARAMETER EngineUrl
    Base URL of the running Tuvima Engine API (default: http://localhost:61495).

.PARAMETER WatchDirectory
    Path to the watch folder. Overrides config auto-detection.

.PARAMETER NoWipe
    Skip the automatic wipe of library.db and library/staging folders.
    By default the script wipes automatically before running.

.PARAMETER WipeFirst
    No-op (kept for backward compatibility). Wipe now happens by default unless -NoWipe is set.

.PARAMETER Force
    No-op (kept for backward compatibility). Wipe no longer requires confirmation.

.PARAMETER TimeoutSeconds
    Maximum seconds to wait for ingestion to complete (default: 120).

.PARAMETER ReportPath
    Where to save the text report. Defaults to tools/reports/book-ingestion-<timestamp>.txt.

.EXAMPLE
    # Standard 30-book test with auto-wipe
    .\tools\Test-BookIngestion.ps1

    # 10-book test, skip the wipe
    .\tools\Test-BookIngestion.ps1 -Count 10 -NoWipe

    # Reproducible run with fixed seed
    .\tools\Test-BookIngestion.ps1 -Seed 42

.NOTES
    Standard run command:
      .\tools\Test-BookIngestion.ps1
      .\tools\Test-BookIngestion.ps1 -Count 10 -NoWipe
      .\tools\Test-BookIngestion.ps1 -Seed 42

    Run as "book ingestion test" - part of the Tuvima testing arsenal.
    Always produces two reports: pre-ingestion (files dropped in) and
    post-ingestion (status of each file after the engine processes it).
#>
[CmdletBinding()]
param(
    [ValidateRange(1, 110)]
    [int]$Count = 30,

    [int]$Seed = -1,

    [string]$EngineUrl = "http://localhost:61495",

    [string]$WatchDirectory = "",

    [switch]$NoWipe,

    [switch]$WipeFirst,   # no-op: kept for backward compatibility

    [switch]$Force,       # no-op: kept for backward compatibility

    [ValidateRange(10, 600)]
    [int]$TimeoutSeconds = 120,

    [string]$ReportPath = ""
)

$ErrorActionPreference = "Stop"

# Wipe is the default; suppress with -NoWipe
$doWipe = -not $NoWipe

# Track the engine process started by this script (so we can shut it down at the end)
$script:EngineProcess = $null

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

# ---------------------------------------------------------------------------
# Resolve paths
# ---------------------------------------------------------------------------
$ScriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Definition
$RepoRoot   = Split-Path -Parent $ScriptRoot
$Timestamp  = Get-Date -Format "yyyy-MM-dd_HH-mm-ss"

if (-not $ReportPath) {
    $ReportsDir = Join-Path $ScriptRoot "reports"
    if (-not (Test-Path $ReportsDir)) { New-Item -ItemType Directory -Path $ReportsDir | Out-Null }
    $ReportPath = Join-Path $ReportsDir "book-ingestion-$Timestamp.txt"
}

$ReportLines = New-Object System.Collections.Generic.List[string]

# ---------------------------------------------------------------------------
# Output helpers
# ---------------------------------------------------------------------------
$Bar72 = "=" * 72

function Write-H {
    param([string]$t, [string]$c = "Cyan")
    Write-Host $Bar72 -ForegroundColor $c
    Write-Host " $t" -ForegroundColor $c
    Write-Host $Bar72 -ForegroundColor $c
    $script:ReportLines.Add($Bar72)
    $script:ReportLines.Add(" $t")
    $script:ReportLines.Add($Bar72)
}

function Write-S {
    param([string]$t)
    $h = "`n -- $t"
    $u = " " + ("-" * 70)
    Write-Host $h -ForegroundColor Yellow
    Write-Host $u -ForegroundColor DarkYellow
    $script:ReportLines.Add("")
    $script:ReportLines.Add(" -- $t")
    $script:ReportLines.Add($u)
}

function Write-R {
    param([string]$t, [string]$c = "White")
    Write-Host $t -ForegroundColor $c
    $script:ReportLines.Add($t)
}

function Write-RL {
    param([string]$t = "")
    Write-Host $t
    $script:ReportLines.Add($t)
}

function Resolve-BookLibrarySourceFromSettings {
    param($Settings)

    if ($null -eq $Settings) { return $null }

    $bookLibrary = @($Settings.libraries) |
        Where-Object {
            $_.category -eq "Books" -or
            @($_.media_types) -contains "Books"
        } |
        Select-Object -First 1
    if ($null -eq $bookLibrary) { return $null }

    $sources = @($bookLibrary.sources) |
        Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_.path) }
    $source = $sources |
        Sort-Object @{ Expression = {
            if ($_.id -eq $bookLibrary.primary_destination_source_id) { 0 }
            else { 1 }
        } } |
        Select-Object -First 1
    if ($source) { return [string]$source.path }

    return $null
}

# ---------------------------------------------------------------------------
# API helper
# ---------------------------------------------------------------------------
function Invoke-Api {
    param([string]$Path, [string]$Method = "GET")
    try {
        return Invoke-RestMethod -Uri "$EngineUrl$Path" -Method $Method -TimeoutSec 10 -ErrorAction Stop
    }
    catch {
        $status = $_.Exception.Response.StatusCode.value__
        if ($status -eq 401 -or $status -eq 403) {
            throw "Development actions need a signed-in administrator. Run this from the Dashboard (Settings > Developer Tools) or use tools/Run-IngestionBaseline.ps1, which runs offline."
        }
        return $null
    }
}

# ---------------------------------------------------------------------------
# Book catalog (100 entries)
# ---------------------------------------------------------------------------
$Catalog = @(
    # HIGH CONFIDENCE - full metadata (60 books)
    [pscustomobject]@{Id=1;  Title="Dune";                                 Author="Frank Herbert";       Year="1965"; Series="Dune Chronicles";         Pos="1"; Isbn="9780441013593"; Genre="Science Fiction"; S="high"; Language="en"; ForeignLanguage=$false; Format="Epub"; Narrator=""; Asin=""; PairedWith=$null}
    [pscustomobject]@{Id=2;  Title="Foundation";                           Author="Isaac Asimov";        Year="1951"; Series="Foundation";              Pos="1"; Isbn="9780553293357"; Genre="Science Fiction"; S="high"; Language="en"; ForeignLanguage=$false; Format="Epub"; Narrator=""; Asin=""; PairedWith=$null}
    [pscustomobject]@{Id=3;  Title="Leviathan Wakes";                      Author="James S.A. Corey";    Year="2011"; Series="The Expanse";             Pos="1"; Isbn="9780316129084"; Genre="Science Fiction"; S="high"; Language="en"; ForeignLanguage=$false; Format="Epub"; Narrator=""; Asin=""; PairedWith=$null}
    [pscustomobject]@{Id=4;  Title="The Name of the Wind";                 Author="Patrick Rothfuss";    Year="2007"; Series="Kingkiller Chronicle";    Pos="1"; Isbn="9780756404079"; Genre="Fantasy";         S="high"; Language="en"; ForeignLanguage=$false; Format="Epub"; Narrator=""; Asin=""; PairedWith=$null}
    [pscustomobject]@{Id=5;  Title="A Game of Thrones";                    Author="George R.R. Martin";  Year="1996"; Series="A Song of Ice and Fire"; Pos="1"; Isbn="9780553381689"; Genre="Fantasy";         S="high"; Language="en"; ForeignLanguage=$false; Format="Epub"; Narrator=""; Asin=""; PairedWith=$null}
    [pscustomobject]@{Id=6;  Title="The Way of Kings";                     Author="Brandon Sanderson";   Year="2010"; Series="The Stormlight Archive"; Pos="1"; Isbn="9780765326355"; Genre="Fantasy";         S="high"; Language="en"; ForeignLanguage=$false; Format="Epub"; Narrator=""; Asin=""; PairedWith=$null}
    [pscustomobject]@{Id=7;  Title="Mistborn: The Final Empire";           Author="Brandon Sanderson";   Year="2006"; Series="Mistborn";               Pos="1"; Isbn="9780765311788"; Genre="Fantasy";         S="high"; Language="en"; ForeignLanguage=$false; Format="Epub"; Narrator=""; Asin=""; PairedWith=$null}
    [pscustomobject]@{Id=8;  Title="The Hitchhiker's Guide to the Galaxy"; Author="Douglas Adams";       Year="1979"; Series="Hitchhiker's Guide";     Pos="1"; Isbn="9780345391803"; Genre="Comedy";          S="high"; Language="en"; ForeignLanguage=$false; Format="Epub"; Narrator=""; Asin=""; PairedWith=$null}
    [pscustomobject]@{Id=9;  Title="Neuromancer";                          Author="William Gibson";      Year="1984"; Series="";                        Pos="";  Isbn="9780441569595"; Genre="Cyberpunk";       S="high"; Language="en"; ForeignLanguage=$false; Format="Epub"; Narrator=""; Asin=""; PairedWith=$null}
    [pscustomobject]@{Id=10; Title="Ender's Game";                         Author="Orson Scott Card";    Year="1985"; Series="Ender's Game";            Pos="1"; Isbn="9780312853235"; Genre="Science Fiction"; S="high"; Language="en"; ForeignLanguage=$false; Format="Epub"; Narrator=""; Asin=""; PairedWith=$null}
    [pscustomobject]@{Id=11; Title="The Lord of the Rings";                Author="J.R.R. Tolkien";      Year="1954"; Series="Middle-earth";            Pos="1"; Isbn="9780261102354"; Genre="Fantasy";         S="high"; Language="en"; ForeignLanguage=$false; Format="Epub"; Narrator=""; Asin=""; PairedWith=$null}
    [pscustomobject]@{Id=12; Title="1984";                                 Author="George Orwell";       Year="1949"; Series="";                        Pos="";  Isbn="9780451524935"; Genre="Dystopian";       S="high"; Language="en"; ForeignLanguage=$false; Format="Epub"; Narrator=""; Asin=""; PairedWith=$null}
    [pscustomobject]@{Id=13; Title="Brave New World";                      Author="Aldous Huxley";       Year="1932"; Series="";                        Pos="";  Isbn="9780060850524"; Genre="Dystopian";       S="high"; Language="en"; ForeignLanguage=$false; Format="Epub"; Narrator=""; Asin=""; PairedWith=$null}
    [pscustomobject]@{Id=14; Title="The Martian";                          Author="Andy Weir";           Year="2011"; Series="";                        Pos="";  Isbn="9780553418026"; Genre="Science Fiction"; S="high"; Language="en"; ForeignLanguage=$false; Format="Epub"; Narrator=""; Asin=""; PairedWith=$null}
    [pscustomobject]@{Id=15; Title="Project Hail Mary";                    Author="Andy Weir";           Year="2021"; Series="";                        Pos="";  Isbn="9780593135204"; Genre="Science Fiction"; S="high"; Language="en"; ForeignLanguage=$false; Format="Epub"; Narrator=""; Asin=""; PairedWith=$null}
    [pscustomobject]@{Id=16; Title="Red Rising";                           Author="Pierce Brown";        Year="2014"; Series="Red Rising Saga";         Pos="1"; Isbn="9780345539786"; Genre="Science Fiction"; S="high"; Language="en"; ForeignLanguage=$false; Format="Epub"; Narrator=""; Asin=""; PairedWith=$null}
    [pscustomobject]@{Id=17; Title="The Blade Itself";                     Author="Joe Abercrombie";     Year="2006"; Series="The First Law";           Pos="1"; Isbn="9780575079793"; Genre="Fantasy";         S="high"; Language="en"; ForeignLanguage=$false; Format="Epub"; Narrator=""; Asin=""; PairedWith=$null}
    [pscustomobject]@{Id=18; Title="Words of Radiance";                    Author="Brandon Sanderson";   Year="2014"; Series="The Stormlight Archive"; Pos="2"; Isbn="9780765326362"; Genre="Fantasy";         S="high"; Language="en"; ForeignLanguage=$false; Format="Epub"; Narrator=""; Asin=""; PairedWith=$null}
    [pscustomobject]@{Id=19; Title="Caliban's War";                        Author="James S.A. Corey";    Year="2012"; Series="The Expanse";             Pos="2"; Isbn="9780316202107"; Genre="Science Fiction"; S="high"; Language="en"; ForeignLanguage=$false; Format="Epub"; Narrator=""; Asin=""; PairedWith=$null}
    [pscustomobject]@{Id=20; Title="Dune Messiah";                         Author="Frank Herbert";       Year="1969"; Series="Dune Chronicles";         Pos="2"; Isbn="9780441172696"; Genre="Science Fiction"; S="high"; Language="en"; ForeignLanguage=$false; Format="Epub"; Narrator=""; Asin=""; PairedWith=$null}
    [pscustomobject]@{Id=21; Title="Foundation and Empire";                Author="Isaac Asimov";        Year="1952"; Series="Foundation";              Pos="2"; Isbn="9780553293371"; Genre="Science Fiction"; S="high"; Language="en"; ForeignLanguage=$false; Format="Epub"; Narrator=""; Asin=""; PairedWith=$null}
    [pscustomobject]@{Id=22; Title="The Well of Ascension";                Author="Brandon Sanderson";   Year="2007"; Series="Mistborn";               Pos="2"; Isbn="9780765316882"; Genre="Fantasy";         S="high"; Language="en"; ForeignLanguage=$false; Format="Epub"; Narrator=""; Asin=""; PairedWith=$null}
    [pscustomobject]@{Id=23; Title="The Eye of the World";                 Author="Robert Jordan";       Year="1990"; Series="The Wheel of Time";      Pos="1"; Isbn="9780765334343"; Genre="Fantasy";         S="high"; Language="en"; ForeignLanguage=$false; Format="Epub"; Narrator=""; Asin=""; PairedWith=$null}
    [pscustomobject]@{Id=24; Title="The Great Hunt";                       Author="Robert Jordan";       Year="1990"; Series="The Wheel of Time";      Pos="2"; Isbn="9780765334350"; Genre="Fantasy";         S="high"; Language="en"; ForeignLanguage=$false; Format="Epub"; Narrator=""; Asin=""; PairedWith=$null}
    [pscustomobject]@{Id=25; Title="A Wizard of Earthsea";                 Author="Ursula K. Le Guin";   Year="1968"; Series="Earthsea Cycle";         Pos="1"; Isbn="9780553383041"; Genre="Fantasy";         S="high"; Language="en"; ForeignLanguage=$false; Format="Epub"; Narrator=""; Asin=""; PairedWith=$null}
    [pscustomobject]@{Id=26; Title="The Left Hand of Darkness";            Author="Ursula K. Le Guin";   Year="1969"; Series="Hainish Cycle";          Pos="";  Isbn="9780441478125"; Genre="Science Fiction"; S="high"; Language="en"; ForeignLanguage=$false; Format="Epub"; Narrator=""; Asin=""; PairedWith=$null}
    [pscustomobject]@{Id=27; Title="Flowers for Algernon";                 Author="Daniel Keyes";        Year="1966"; Series="";                        Pos="";  Isbn="9780156030304"; Genre="Science Fiction"; S="high"; Language="en"; ForeignLanguage=$false; Format="Epub"; Narrator=""; Asin=""; PairedWith=$null}
    [pscustomobject]@{Id=28; Title="Slaughterhouse-Five";                  Author="Kurt Vonnegut";       Year="1969"; Series="";                        Pos="";  Isbn="9780440180296"; Genre="Science Fiction"; S="high"; Language="en"; ForeignLanguage=$false; Format="Epub"; Narrator=""; Asin=""; PairedWith=$null}
    [pscustomobject]@{Id=29; Title="The Hobbit";                           Author="J.R.R. Tolkien";      Year="1937"; Series="Middle-earth";            Pos="";  Isbn="9780547928227"; Genre="Fantasy";         S="high"; Language="en"; ForeignLanguage=$false; Format="Epub"; Narrator=""; Asin=""; PairedWith=$null}
    [pscustomobject]@{Id=30; Title="The Color of Magic";                   Author="Terry Pratchett";     Year="1983"; Series="Discworld";              Pos="1"; Isbn="9780062225672"; Genre="Fantasy Comedy";  S="high"; Language="en"; ForeignLanguage=$false; Format="Epub"; Narrator=""; Asin=""; PairedWith=$null}
    [pscustomobject]@{Id=31; Title="Good Omens";                           Author="Terry Pratchett";     Year="1990"; Series="";                        Pos="";  Isbn="9780060853983"; Genre="Fantasy Comedy";  S="high"; Language="en"; ForeignLanguage=$false; Format="Epub"; Narrator=""; Asin=""; PairedWith=$null}
    [pscustomobject]@{Id=32; Title="American Gods";                        Author="Neil Gaiman";         Year="2001"; Series="";                        Pos="";  Isbn="9780380973651"; Genre="Fantasy";         S="high"; Language="en"; ForeignLanguage=$false; Format="Epub"; Narrator=""; Asin=""; PairedWith=$null}
    [pscustomobject]@{Id=33; Title="The Lies of Locke Lamora";             Author="Scott Lynch";         Year="2006"; Series="Gentleman Bastard";      Pos="1"; Isbn="9780553588941"; Genre="Fantasy";         S="high"; Language="en"; ForeignLanguage=$false; Format="Epub"; Narrator=""; Asin=""; PairedWith=$null}
    [pscustomobject]@{Id=34; Title="Assassin's Apprentice";                Author="Robin Hobb";          Year="1995"; Series="Farseer Trilogy";        Pos="1"; Isbn="9780553573398"; Genre="Fantasy";         S="high"; Language="en"; ForeignLanguage=$false; Format="Epub"; Narrator=""; Asin=""; PairedWith=$null}
    [pscustomobject]@{Id=35; Title="Ancillary Justice";                    Author="Ann Leckie";          Year="2013"; Series="Imperial Radch";         Pos="1"; Isbn="9780316246620"; Genre="Science Fiction"; S="high"; Language="en"; ForeignLanguage=$false; Format="Epub"; Narrator=""; Asin=""; PairedWith=$null}
    [pscustomobject]@{Id=36; Title="The Dispossessed";                     Author="Ursula K. Le Guin";   Year="1974"; Series="Hainish Cycle";          Pos="";  Isbn="9780061054884"; Genre="Science Fiction"; S="high"; Language="en"; ForeignLanguage=$false; Format="Epub"; Narrator=""; Asin=""; PairedWith=$null}
    [pscustomobject]@{Id=37; Title="Hyperion";                             Author="Dan Simmons";         Year="1989"; Series="Hyperion Cantos";        Pos="1"; Isbn="9780553283686"; Genre="Science Fiction"; S="high"; Language="en"; ForeignLanguage=$false; Format="Epub"; Narrator=""; Asin=""; PairedWith=$null}
    [pscustomobject]@{Id=38; Title="The Fall of Hyperion";                 Author="Dan Simmons";         Year="1990"; Series="Hyperion Cantos";        Pos="2"; Isbn="9780553288208"; Genre="Science Fiction"; S="high"; Language="en"; ForeignLanguage=$false; Format="Epub"; Narrator=""; Asin=""; PairedWith=$null}
    [pscustomobject]@{Id=39; Title="Snow Crash";                           Author="Neal Stephenson";     Year="1992"; Series="";                        Pos="";  Isbn="9780553380958"; Genre="Cyberpunk";       S="high"; Language="en"; ForeignLanguage=$false; Format="Epub"; Narrator=""; Asin=""; PairedWith=$null}
    [pscustomobject]@{Id=40; Title="Cryptonomicon";                        Author="Neal Stephenson";     Year="1999"; Series="";                        Pos="";  Isbn="9780060512804"; Genre="Thriller";        S="high"; Language="en"; ForeignLanguage=$false; Format="Epub"; Narrator=""; Asin=""; PairedWith=$null}
    [pscustomobject]@{Id=41; Title="Old Man's War";                        Author="John Scalzi";         Year="2005"; Series="Old Man's War";          Pos="1"; Isbn="9780765315034"; Genre="Science Fiction"; S="high"; Language="en"; ForeignLanguage=$false; Format="Epub"; Narrator=""; Asin=""; PairedWith=$null}
    [pscustomobject]@{Id=42; Title="The Android's Dream";                  Author="John Scalzi";         Year="2006"; Series="";                        Pos="";  Isbn="9780765348494"; Genre="Science Fiction"; S="high"; Language="en"; ForeignLanguage=$false; Format="Epub"; Narrator=""; Asin=""; PairedWith=$null}
    [pscustomobject]@{Id=43; Title="Piranesi";                             Author="Susanna Clarke";      Year="2020"; Series="";                        Pos="";  Isbn="9781635575637"; Genre="Fantasy";         S="high"; Language="en"; ForeignLanguage=$false; Format="Epub"; Narrator=""; Asin=""; PairedWith=$null}
    [pscustomobject]@{Id=44; Title="The Fifth Season";                     Author="N.K. Jemisin";        Year="2015"; Series="The Broken Earth";       Pos="1"; Isbn="9780316229296"; Genre="Science Fiction"; S="high"; Language="en"; ForeignLanguage=$false; Format="Epub"; Narrator=""; Asin=""; PairedWith=$null}
    [pscustomobject]@{Id=45; Title="All Systems Red";                      Author="Martha Wells";        Year="2017"; Series="Murderbot Diaries";      Pos="1"; Isbn="9780765397539"; Genre="Science Fiction"; S="high"; Language="en"; ForeignLanguage=$false; Format="Epub"; Narrator=""; Asin=""; PairedWith=$null}
    [pscustomobject]@{Id=46; Title="A Memory Called Empire";               Author="Arkady Martine";      Year="2019"; Series="Teixcalaan";             Pos="1"; Isbn="9781250186430"; Genre="Science Fiction"; S="high"; Language="en"; ForeignLanguage=$false; Format="Epub"; Narrator=""; Asin=""; PairedWith=$null}
    [pscustomobject]@{Id=47; Title="The Long Way to a Small Angry Planet"; Author="Becky Chambers";      Year="2014"; Series="Wayfarers";             Pos="1"; Isbn="9781473619814"; Genre="Science Fiction"; S="high"; Language="en"; ForeignLanguage=$false; Format="Epub"; Narrator=""; Asin=""; PairedWith=$null}
    [pscustomobject]@{Id=48; Title="Klara and the Sun";                    Author="Kazuo Ishiguro";      Year="2021"; Series="";                        Pos="";  Isbn="9780593311295"; Genre="Science Fiction"; S="high"; Language="en"; ForeignLanguage=$false; Format="Epub"; Narrator=""; Asin=""; PairedWith=$null}
    [pscustomobject]@{Id=49; Title="The Buried Giant";                     Author="Kazuo Ishiguro";      Year="2015"; Series="";                        Pos="";  Isbn="9780307455796"; Genre="Fantasy";         S="high"; Language="en"; ForeignLanguage=$false; Format="Epub"; Narrator=""; Asin=""; PairedWith=$null}
    [pscustomobject]@{Id=50; Title="Jonathan Strange and Mr Norrell";      Author="Susanna Clarke";      Year="2004"; Series="";                        Pos="";  Isbn="9781582344164"; Genre="Fantasy";         S="high"; Language="en"; ForeignLanguage=$false; Format="Epub"; Narrator=""; Asin=""; PairedWith=$null}
    [pscustomobject]@{Id=51; Title="Children of Time";                     Author="Adrian Tchaikovsky";  Year="2015"; Series="Children of Time";       Pos="1"; Isbn="9781447273288"; Genre="Science Fiction"; S="high"; Language="en"; ForeignLanguage=$false; Format="Epub"; Narrator=""; Asin=""; PairedWith=$null}
    [pscustomobject]@{Id=52; Title="To Be Taught If Fortunate";            Author="Becky Chambers";      Year="2020"; Series="";                        Pos="";  Isbn="9781250236234"; Genre="Science Fiction"; S="high"; Language="en"; ForeignLanguage=$false; Format="Epub"; Narrator=""; Asin=""; PairedWith=$null}
    [pscustomobject]@{Id=53; Title="The Ninth Rain";                       Author="Jen Williams";        Year="2017"; Series="The Winnowing Flame";    Pos="1"; Isbn="9781472235299"; Genre="Fantasy";         S="high"; Language="en"; ForeignLanguage=$false; Format="Epub"; Narrator=""; Asin=""; PairedWith=$null}
    [pscustomobject]@{Id=54; Title="Abaddon's Gate";                       Author="James S.A. Corey";    Year="2013"; Series="The Expanse";             Pos="3"; Isbn="9780316129060"; Genre="Science Fiction"; S="high"; Language="en"; ForeignLanguage=$false; Format="Epub"; Narrator=""; Asin=""; PairedWith=$null}
    [pscustomobject]@{Id=55; Title="Second Foundation";                    Author="Isaac Asimov";        Year="1953"; Series="Foundation";              Pos="3"; Isbn="9780553293388"; Genre="Science Fiction"; S="high"; Language="en"; ForeignLanguage=$false; Format="Epub"; Narrator=""; Asin=""; PairedWith=$null}
    [pscustomobject]@{Id=56; Title="Children of Dune";                     Author="Frank Herbert";       Year="1976"; Series="Dune Chronicles";         Pos="3"; Isbn="9780441104024"; Genre="Science Fiction"; S="high"; Language="en"; ForeignLanguage=$false; Format="Epub"; Narrator=""; Asin=""; PairedWith=$null}
    [pscustomobject]@{Id=57; Title="The Hero of Ages";                     Author="Brandon Sanderson";   Year="2008"; Series="Mistborn";               Pos="3"; Isbn="9780765356147"; Genre="Fantasy";         S="high"; Language="en"; ForeignLanguage=$false; Format="Epub"; Narrator=""; Asin=""; PairedWith=$null}
    [pscustomobject]@{Id=58; Title="Oathbringer";                          Author="Brandon Sanderson";   Year="2017"; Series="The Stormlight Archive"; Pos="3"; Isbn="9780765326379"; Genre="Fantasy";         S="high"; Language="en"; ForeignLanguage=$false; Format="Epub"; Narrator=""; Asin=""; PairedWith=$null}
    [pscustomobject]@{Id=59; Title="The Dragon Reborn";                    Author="Robert Jordan";       Year="1991"; Series="The Wheel of Time";      Pos="3"; Isbn="9780765334367"; Genre="Fantasy";         S="high"; Language="en"; ForeignLanguage=$false; Format="Epub"; Narrator=""; Asin=""; PairedWith=$null}
    [pscustomobject]@{Id=60; Title="Before They Are Hanged";               Author="Joe Abercrombie";     Year="2007"; Series="The First Law";           Pos="2"; Isbn="9781591025788"; Genre="Fantasy";         S="high"; Language="en"; ForeignLanguage=$false; Format="Epub"; Narrator=""; Asin=""; PairedWith=$null}
    # MEDIUM CONFIDENCE - title + author + year (20 books)
    # IDs 61-63, 72-80: foreign-language origin (ForeignLanguage=$true)
    [pscustomobject]@{Id=61; Title="The Alchemist";               Author="Paulo Coelho";        Year="1988"; Series=""; Pos=""; Isbn=""; Genre=""; S="medium"; Language="pt"; ForeignLanguage=$true; Format="Epub"; Narrator=""; Asin=""; PairedWith=$null}
    [pscustomobject]@{Id=62; Title="Siddhartha";                  Author="Hermann Hesse";       Year="1922"; Series=""; Pos=""; Isbn=""; Genre=""; S="medium"; Language="de"; ForeignLanguage=$true; Format="Epub"; Narrator=""; Asin=""; PairedWith=$null}
    [pscustomobject]@{Id=63; Title="The Trial";                   Author="Franz Kafka";         Year="1925"; Series=""; Pos=""; Isbn=""; Genre=""; S="medium"; Language="de"; ForeignLanguage=$true; Format="Epub"; Narrator=""; Asin=""; PairedWith=$null}
    [pscustomobject]@{Id=64; Title="Invisible Man";               Author="Ralph Ellison";       Year="1952"; Series=""; Pos=""; Isbn=""; Genre=""; S="medium"; Language="en"; ForeignLanguage=$false; Format="Epub"; Narrator=""; Asin=""; PairedWith=$null}
    [pscustomobject]@{Id=65; Title="The Bell Jar";                Author="Sylvia Plath";        Year="1963"; Series=""; Pos=""; Isbn=""; Genre=""; S="medium"; Language="en"; ForeignLanguage=$false; Format="Epub"; Narrator=""; Asin=""; PairedWith=$null}
    [pscustomobject]@{Id=66; Title="A Farewell to Arms";          Author="Ernest Hemingway";    Year="1929"; Series=""; Pos=""; Isbn=""; Genre=""; S="medium"; Language="en"; ForeignLanguage=$false; Format="Epub"; Narrator=""; Asin=""; PairedWith=$null}
    [pscustomobject]@{Id=67; Title="For Whom the Bell Tolls";     Author="Ernest Hemingway";    Year="1940"; Series=""; Pos=""; Isbn=""; Genre=""; S="medium"; Language="en"; ForeignLanguage=$false; Format="Epub"; Narrator=""; Asin=""; PairedWith=$null}
    [pscustomobject]@{Id=68; Title="The Old Man and the Sea";     Author="Ernest Hemingway";    Year="1952"; Series=""; Pos=""; Isbn=""; Genre=""; S="medium"; Language="en"; ForeignLanguage=$false; Format="Epub"; Narrator=""; Asin=""; PairedWith=$null}
    [pscustomobject]@{Id=69; Title="Of Mice and Men";             Author="John Steinbeck";      Year="1937"; Series=""; Pos=""; Isbn=""; Genre=""; S="medium"; Language="en"; ForeignLanguage=$false; Format="Epub"; Narrator=""; Asin=""; PairedWith=$null}
    [pscustomobject]@{Id=70; Title="East of Eden";                Author="John Steinbeck";      Year="1952"; Series=""; Pos=""; Isbn=""; Genre=""; S="medium"; Language="en"; ForeignLanguage=$false; Format="Epub"; Narrator=""; Asin=""; PairedWith=$null}
    [pscustomobject]@{Id=71; Title="Moby Dick";                   Author="Herman Melville";     Year="1851"; Series=""; Pos=""; Isbn=""; Genre=""; S="medium"; Language="en"; ForeignLanguage=$false; Format="Epub"; Narrator=""; Asin=""; PairedWith=$null}
    [pscustomobject]@{Id=72; Title="Crime and Punishment";        Author="Fyodor Dostoevsky";   Year="1866"; Series=""; Pos=""; Isbn=""; Genre=""; S="medium"; Language="ru"; ForeignLanguage=$true; Format="Epub"; Narrator=""; Asin=""; PairedWith=$null}
    [pscustomobject]@{Id=73; Title="The Brothers Karamazov";      Author="Fyodor Dostoevsky";   Year="1879"; Series=""; Pos=""; Isbn=""; Genre=""; S="medium"; Language="ru"; ForeignLanguage=$true; Format="Epub"; Narrator=""; Asin=""; PairedWith=$null}
    [pscustomobject]@{Id=74; Title="War and Peace";               Author="Leo Tolstoy";         Year="1869"; Series=""; Pos=""; Isbn=""; Genre=""; S="medium"; Language="ru"; ForeignLanguage=$true; Format="Epub"; Narrator=""; Asin=""; PairedWith=$null}
    [pscustomobject]@{Id=75; Title="Anna Karenina";               Author="Leo Tolstoy";         Year="1877"; Series=""; Pos=""; Isbn=""; Genre=""; S="medium"; Language="ru"; ForeignLanguage=$true; Format="Epub"; Narrator=""; Asin=""; PairedWith=$null}
    [pscustomobject]@{Id=76; Title="Don Quixote";                 Author="Miguel de Cervantes"; Year="1605"; Series=""; Pos=""; Isbn=""; Genre=""; S="medium"; Language="es"; ForeignLanguage=$true; Format="Epub"; Narrator=""; Asin=""; PairedWith=$null}
    [pscustomobject]@{Id=77; Title="The Divine Comedy";           Author="Dante Alighieri";     Year="1320"; Series=""; Pos=""; Isbn=""; Genre=""; S="medium"; Language="it"; ForeignLanguage=$true; Format="Epub"; Narrator=""; Asin=""; PairedWith=$null}
    [pscustomobject]@{Id=78; Title="Les Miserables";              Author="Victor Hugo";         Year="1862"; Series=""; Pos=""; Isbn=""; Genre=""; S="medium"; Language="fr"; ForeignLanguage=$true; Format="Epub"; Narrator=""; Asin=""; PairedWith=$null}
    [pscustomobject]@{Id=79; Title="The Count of Monte Cristo";   Author="Alexandre Dumas";     Year="1844"; Series=""; Pos=""; Isbn=""; Genre=""; S="medium"; Language="fr"; ForeignLanguage=$true; Format="Epub"; Narrator=""; Asin=""; PairedWith=$null}
    [pscustomobject]@{Id=80; Title="Around the World in 80 Days"; Author="Jules Verne";         Year="1872"; Series=""; Pos=""; Isbn=""; Genre=""; S="medium"; Language="fr"; ForeignLanguage=$true; Format="Epub"; Narrator=""; Asin=""; PairedWith=$null}
    # LOW CONFIDENCE - no embedded metadata (10 books)
    [pscustomobject]@{Id=81; Title="unlabeled_scan_001";          Author=""; Year=""; Series=""; Pos=""; Isbn=""; Genre=""; S="low"; Language="en"; ForeignLanguage=$false; Format="Epub"; Narrator=""; Asin=""; PairedWith=$null}
    [pscustomobject]@{Id=82; Title="ebook_download_final";        Author=""; Year=""; Series=""; Pos=""; Isbn=""; Genre=""; S="low"; Language="en"; ForeignLanguage=$false; Format="Epub"; Narrator=""; Asin=""; PairedWith=$null}
    [pscustomobject]@{Id=83; Title="converted_doc_v2";            Author=""; Year=""; Series=""; Pos=""; Isbn=""; Genre=""; S="low"; Language="en"; ForeignLanguage=$false; Format="Epub"; Narrator=""; Asin=""; PairedWith=$null}
    [pscustomobject]@{Id=84; Title="reading_list_item_3";         Author=""; Year=""; Series=""; Pos=""; Isbn=""; Genre=""; S="low"; Language="en"; ForeignLanguage=$false; Format="Epub"; Narrator=""; Asin=""; PairedWith=$null}
    [pscustomobject]@{Id=85; Title="backup_book_copy";            Author=""; Year=""; Series=""; Pos=""; Isbn=""; Genre=""; S="low"; Language="en"; ForeignLanguage=$false; Format="Epub"; Narrator=""; Asin=""; PairedWith=$null}
    [pscustomobject]@{Id=86; Title="mystery_epub_untitled";       Author=""; Year=""; Series=""; Pos=""; Isbn=""; Genre=""; S="low"; Language="en"; ForeignLanguage=$false; Format="Epub"; Narrator=""; Asin=""; PairedWith=$null}
    [pscustomobject]@{Id=87; Title="document_export_003";         Author=""; Year=""; Series=""; Pos=""; Isbn=""; Genre=""; S="low"; Language="en"; ForeignLanguage=$false; Format="Epub"; Narrator=""; Asin=""; PairedWith=$null}
    [pscustomobject]@{Id=88; Title="temp_file_do_not_delete";     Author=""; Year=""; Series=""; Pos=""; Isbn=""; Genre=""; S="low"; Language="en"; ForeignLanguage=$false; Format="Epub"; Narrator=""; Asin=""; PairedWith=$null}
    [pscustomobject]@{Id=89; Title="new_book_no_title";           Author=""; Year=""; Series=""; Pos=""; Isbn=""; Genre=""; S="low"; Language="en"; ForeignLanguage=$false; Format="Epub"; Narrator=""; Asin=""; PairedWith=$null}
    [pscustomobject]@{Id=90; Title="archive_entry_2024";          Author=""; Year=""; Series=""; Pos=""; Isbn=""; Genre=""; S="low"; Language="en"; ForeignLanguage=$false; Format="Epub"; Narrator=""; Asin=""; PairedWith=$null}
    # CORRUPT - invalid file bytes (5 books)
    [pscustomobject]@{Id=91; Title="corrupted_epub_A"; Author=""; Year=""; Series=""; Pos=""; Isbn=""; Genre=""; S="corrupt"; Language="en"; ForeignLanguage=$false; Format="Epub"; Narrator=""; Asin=""; PairedWith=$null}
    [pscustomobject]@{Id=92; Title="corrupted_epub_B"; Author=""; Year=""; Series=""; Pos=""; Isbn=""; Genre=""; S="corrupt"; Language="en"; ForeignLanguage=$false; Format="Epub"; Narrator=""; Asin=""; PairedWith=$null}
    [pscustomobject]@{Id=93; Title="truncated_file";   Author=""; Year=""; Series=""; Pos=""; Isbn=""; Genre=""; S="corrupt"; Language="en"; ForeignLanguage=$false; Format="Epub"; Narrator=""; Asin=""; PairedWith=$null}
    [pscustomobject]@{Id=94; Title="wrong_magic_bytes";Author=""; Year=""; Series=""; Pos=""; Isbn=""; Genre=""; S="corrupt"; Language="en"; ForeignLanguage=$false; Format="Epub"; Narrator=""; Asin=""; PairedWith=$null}
    [pscustomobject]@{Id=95; Title="empty_epub";       Author=""; Year=""; Series=""; Pos=""; Isbn=""; Genre=""; S="corrupt"; Language="en"; ForeignLanguage=$false; Format="Epub"; Narrator=""; Asin=""; PairedWith=$null}
    # DUPLICATES - identical bytes to books 1-5 (5 books)
    [pscustomobject]@{Id=96;  Title="Dune";            Author="Frank Herbert";    Year="1965"; Series="Dune Chronicles"; Pos="1"; Isbn="9780441013593"; Genre="Science Fiction"; S="duplicate"; DuplicateOf=1;  Language="en"; ForeignLanguage=$false; Format="Epub"; Narrator=""; Asin=""; PairedWith=$null}
    [pscustomobject]@{Id=97;  Title="Foundation";      Author="Isaac Asimov";     Year="1951"; Series="Foundation";      Pos="1"; Isbn="9780553293357"; Genre="Science Fiction"; S="duplicate"; DuplicateOf=2;  Language="en"; ForeignLanguage=$false; Format="Epub"; Narrator=""; Asin=""; PairedWith=$null}
    [pscustomobject]@{Id=98;  Title="Leviathan Wakes"; Author="James S.A. Corey"; Year="2011"; Series="The Expanse";     Pos="1"; Isbn="9780316129084"; Genre="Science Fiction"; S="duplicate"; DuplicateOf=3;  Language="en"; ForeignLanguage=$false; Format="Epub"; Narrator=""; Asin=""; PairedWith=$null}
    [pscustomobject]@{Id=99;  Title="The Martian";     Author="Andy Weir";        Year="2011"; Series="";               Pos="";  Isbn="9780553418026"; Genre="Science Fiction"; S="duplicate"; DuplicateOf=14; Language="en"; ForeignLanguage=$false; Format="Epub"; Narrator=""; Asin=""; PairedWith=$null}
    [pscustomobject]@{Id=100; Title="1984";             Author="George Orwell";    Year="1949"; Series="";               Pos="";  Isbn="9780451524935"; Genre="Dystopian";       S="duplicate"; DuplicateOf=12; Language="en"; ForeignLanguage=$false; Format="Epub"; Narrator=""; Asin=""; PairedWith=$null}
    # AUDIOBOOK entries - M4B format (10 audiobooks)
    # Paired audiobooks — same book exists as EPUB above; tests ebook+audiobook layout
    [pscustomobject]@{Id=101; Title="Dune";                                 Author="Frank Herbert";       Year="1965"; Series="Dune Chronicles";      Pos="1"; Isbn="9780441013593"; Genre="Science Fiction"; S="audiobook"; Language="en"; ForeignLanguage=$false; Format="Audiobook"; Narrator="Scott Brick";       Asin="B002V1OF70"; PairedWith=1}
    [pscustomobject]@{Id=102; Title="Project Hail Mary";                    Author="Andy Weir";           Year="2021"; Series="";                      Pos="";  Isbn="9780593135204"; Genre="Science Fiction"; S="audiobook"; Language="en"; ForeignLanguage=$false; Format="Audiobook"; Narrator="Ray Porter";        Asin="B08G9PRS1K"; PairedWith=15}
    [pscustomobject]@{Id=103; Title="The Hitchhiker's Guide to the Galaxy"; Author="Douglas Adams";       Year="1979"; Series="Hitchhiker's Guide";   Pos="1"; Isbn="9780345391803"; Genre="Comedy";          S="audiobook"; Language="en"; ForeignLanguage=$false; Format="Audiobook"; Narrator="Stephen Fry";       Asin="B0009JKV9W"; PairedWith=8}
    [pscustomobject]@{Id=104; Title="The Name of the Wind";                 Author="Patrick Rothfuss";    Year="2007"; Series="Kingkiller Chronicle";  Pos="1"; Isbn="9780756404079"; Genre="Fantasy";         S="audiobook"; Language="en"; ForeignLanguage=$false; Format="Audiobook"; Narrator="Nick Podehl";       Asin="B002UZMLXM"; PairedWith=4}
    [pscustomobject]@{Id=105; Title="Ender's Game";                         Author="Orson Scott Card";    Year="1985"; Series="Ender's Game";          Pos="1"; Isbn="9780312853235"; Genre="Science Fiction"; S="audiobook"; Language="en"; ForeignLanguage=$false; Format="Audiobook"; Narrator="Stefan Rudnicki"; Asin="B002V5GWHK"; PairedWith=10}
    # Standalone audiobooks — no matching EPUB in catalog
    [pscustomobject]@{Id=106; Title="Born a Crime";           Author="Trevor Noah";     Year="2016"; Series=""; Pos=""; Isbn="9780399588174"; Genre="Memoir";          S="audiobook"; Language="en"; ForeignLanguage=$false; Format="Audiobook"; Narrator="Trevor Noah";       Asin="B01IW9TM5O"; PairedWith=$null}
    [pscustomobject]@{Id=107; Title="Becoming";               Author="Michelle Obama";  Year="2018"; Series=""; Pos=""; Isbn="9781524763138"; Genre="Memoir";          S="audiobook"; Language="en"; ForeignLanguage=$false; Format="Audiobook"; Narrator="Michelle Obama";    Asin="B07B3JQZCL"; PairedWith=$null}
    [pscustomobject]@{Id=108; Title="The Sandman: Act I";     Author="Neil Gaiman";     Year="2020"; Series="The Sandman";  Pos="1"; Isbn=""; Genre="Fantasy Drama";    S="audiobook"; Language="en"; ForeignLanguage=$false; Format="Audiobook"; Narrator="Neil Gaiman";       Asin="B086WP794Z"; PairedWith=$null}
    [pscustomobject]@{Id=109; Title="Sapiens";                Author="Yuval Noah Harari"; Year="2011"; Series=""; Pos=""; Isbn="9780062316097"; Genre="Non-Fiction";    S="audiobook"; Language="en"; ForeignLanguage=$false; Format="Audiobook"; Narrator="Derek Perkins";     Asin="B0741G911Q"; PairedWith=$null}
    [pscustomobject]@{Id=110; Title="Atomic Habits";          Author="James Clear";     Year="2018"; Series=""; Pos=""; Isbn="9780735211292"; Genre="Self-Help";       S="audiobook"; Language="en"; ForeignLanguage=$false; Format="Audiobook"; Narrator="James Clear";      Asin="B07RFSSYBH"; PairedWith=$null}
)

# ---------------------------------------------------------------------------
# EPUB generation
# ---------------------------------------------------------------------------
function New-ValidEpub {
    param([object]$Book, [string]$OutputPath)

    function Escape-Xml([string]$s) {
        return $s.Replace("&","&amp;").Replace("<","&lt;").Replace(">","&gt;").Replace('"',"&quot;").Replace("'","&apos;")
    }

    $et  = Escape-Xml $Book.Title
    $ea  = Escape-Xml $Book.Author
    $seriesmeta = if ($Book.Series) { "    <meta property=`"belongs-to-collection`" id=`"series`">$(Escape-Xml $Book.Series)</meta>`n    <meta property=`"group-position`">$($Book.Pos)</meta>" } else { "" }
    $genremeta  = if ($Book.Genre)  { "    <dc:subject>$(Escape-Xml $Book.Genre)</dc:subject>" } else { "" }
    $isbnid     = if ($Book.Isbn)   { "urn:isbn:$($Book.Isbn)" } else { "urn:uuid:$(([System.Guid]::NewGuid()).ToString())" }

    $container = "<?xml version=`"1.0`" encoding=`"UTF-8`"?>`n<container version=`"1.0`" xmlns=`"urn:oasis:names:tc:opendocument:xmlns:container`">`n  <rootfiles>`n    <rootfile full-path=`"OEBPS/content.opf`" media-type=`"application/oebps-package+xml`"/>`n  </rootfiles>`n</container>"

    $lang = if ($Book.Language) { $Book.Language } else { "en" }
    $opf = "<?xml version=`"1.0`" encoding=`"UTF-8`"?>`n<package version=`"3.0`" xmlns=`"http://www.idpf.org/2007/opf`" unique-identifier=`"uid`">`n  <metadata xmlns:dc=`"http://purl.org/dc/elements/1.1/`" xmlns:opf=`"http://www.idpf.org/2007/opf`">`n    <dc:identifier id=`"uid`">$isbnid</dc:identifier>`n    <dc:title>$et</dc:title>`n    <dc:creator opf:role=`"aut`">$ea</dc:creator>`n    <dc:date>$($Book.Year)</dc:date>`n    <dc:language>$lang</dc:language>`n    $seriesmeta`n    $genremeta`n  </metadata>`n  <manifest>`n    <item id=`"c1`" href=`"chapter1.xhtml`" media-type=`"application/xhtml+xml`"/>`n    <item id=`"nav`" href=`"nav.xhtml`" media-type=`"application/xhtml+xml`" properties=`"nav`"/>`n  </manifest>`n  <spine><itemref idref=`"c1`"/></spine>`n</package>"

    $chapter = "<?xml version=`"1.0`" encoding=`"utf-8`"?><!DOCTYPE html><html xmlns=`"http://www.w3.org/1999/xhtml`"><head><title>$et</title></head><body><p>Synthetic test content for $et by $ea.</p></body></html>"
    $nav = "<?xml version=`"1.0`" encoding=`"utf-8`"?><html xmlns=`"http://www.w3.org/1999/xhtml`" xmlns:epub=`"http://www.idpf.org/2007/ops`"><head><title>Navigation</title></head><body><nav epub:type=`"toc`" id=`"toc`"><ol><li><a href=`"chapter1.xhtml`">Chapter 1</a></li></ol></nav></body></html>"

    $stream = [System.IO.File]::Create($OutputPath)
    try {
        $zip = New-Object System.IO.Compression.ZipArchive($stream, [System.IO.Compression.ZipArchiveMode]::Create, $true)
        try {
            $e = $zip.CreateEntry("mimetype", [System.IO.Compression.CompressionLevel]::NoCompression)
            $w = New-Object System.IO.StreamWriter($e.Open()); $w.Write("application/epub+zip"); $w.Dispose()
            $e = $zip.CreateEntry("META-INF/container.xml")
            $w = New-Object System.IO.StreamWriter($e.Open()); $w.Write($container); $w.Dispose()
            $e = $zip.CreateEntry("OEBPS/content.opf")
            $w = New-Object System.IO.StreamWriter($e.Open()); $w.Write($opf); $w.Dispose()
            $e = $zip.CreateEntry("OEBPS/chapter1.xhtml")
            $w = New-Object System.IO.StreamWriter($e.Open()); $w.Write($chapter); $w.Dispose()
            $e = $zip.CreateEntry("OEBPS/nav.xhtml")
            $w = New-Object System.IO.StreamWriter($e.Open()); $w.Write($nav); $w.Dispose()
        }
        finally { $zip.Dispose() }
    }
    finally { $stream.Dispose() }
}

function New-BareEpub {
    param([string]$OutputPath)
    $container = "<?xml version=`"1.0`" encoding=`"UTF-8`"?><container version=`"1.0`" xmlns=`"urn:oasis:names:tc:opendocument:xmlns:container`"><rootfiles><rootfile full-path=`"OEBPS/content.opf`" media-type=`"application/oebps-package+xml`"/></rootfiles></container>"
    $opf       = "<?xml version=`"1.0`" encoding=`"UTF-8`"?><package version=`"3.0`" xmlns=`"http://www.idpf.org/2007/opf`" unique-identifier=`"uid`"><metadata xmlns:dc=`"http://purl.org/dc/elements/1.1/`"><dc:identifier id=`"uid`">urn:uuid:$(([System.Guid]::NewGuid()).ToString())</dc:identifier><dc:language>en</dc:language></metadata><manifest><item id=`"c1`" href=`"c1.xhtml`" media-type=`"application/xhtml+xml`"/><item id=`"nav`" href=`"nav.xhtml`" media-type=`"application/xhtml+xml`" properties=`"nav`"/></manifest><spine><itemref idref=`"c1`"/></spine></package>"
    $nav       = "<?xml version=`"1.0`" encoding=`"utf-8`"?><html xmlns=`"http://www.w3.org/1999/xhtml`" xmlns:epub=`"http://www.idpf.org/2007/ops`"><head><title>Navigation</title></head><body><nav epub:type=`"toc`" id=`"toc`"><ol><li><a href=`"c1.xhtml`">Content</a></li></ol></nav></body></html>"
    $stream = [System.IO.File]::Create($OutputPath)
    try {
        $zip = New-Object System.IO.Compression.ZipArchive($stream, [System.IO.Compression.ZipArchiveMode]::Create, $true)
        try {
            $e = $zip.CreateEntry("mimetype", [System.IO.Compression.CompressionLevel]::NoCompression)
            $w = New-Object System.IO.StreamWriter($e.Open()); $w.Write("application/epub+zip"); $w.Dispose()
            $e = $zip.CreateEntry("META-INF/container.xml")
            $w = New-Object System.IO.StreamWriter($e.Open()); $w.Write($container); $w.Dispose()
            $e = $zip.CreateEntry("OEBPS/content.opf")
            $w = New-Object System.IO.StreamWriter($e.Open()); $w.Write($opf); $w.Dispose()
            $e = $zip.CreateEntry("OEBPS/c1.xhtml")
            $w = New-Object System.IO.StreamWriter($e.Open()); $w.Write("<html><body><p>No metadata.</p></body></html>"); $w.Dispose()
            $e = $zip.CreateEntry("OEBPS/nav.xhtml")
            $w = New-Object System.IO.StreamWriter($e.Open()); $w.Write($nav); $w.Dispose()
        }
        finally { $zip.Dispose() }
    }
    finally { $stream.Dispose() }
}

function New-SyntheticM4b {
    param([object]$Book, [string]$OutputPath)

    # Build a minimal valid MP4/M4B with iTunes metadata atoms.
    # Structure: ftyp + moov(mvhd + trak(tkhd + mdia(mdhd + hdlr + minf(smhd + dinf(dref) + stbl(stsd+stts+stsc+stsz+stco)))) + udta(meta(hdlr+ilst))) + mdat

    $enc = [System.Text.Encoding]::UTF8

    # --- Helper: big-endian 32-bit integer as 4 bytes ---
    function BE32([int]$v) {
        $b = [BitConverter]::GetBytes($v); [Array]::Reverse($b); return $b
    }

    # --- Helper: build an MP4 box (atom) ---
    function Box([string]$type, [byte[]]$payload) {
        $size = 8 + $payload.Length
        return (BE32 $size) + $enc.GetBytes($type) + $payload
    }

    # --- Helper: full box (version + flags + payload) ---
    function FullBox([string]$type, [byte]$version, [int]$flags, [byte[]]$payload) {
        $flagBytes = BE32 $flags
        $inner = @($version) + $flagBytes[1..3] + $payload
        return Box $type ([byte[]]$inner)
    }

    # --- Helper: iTunes metadata data atom ---
    function DataAtom([string]$value) {
        $utf8 = $enc.GetBytes($value)
        # type indicator: 1 = UTF-8 text
        $inner = (BE32 1) + (BE32 0) + $utf8   # type(4) + locale(4) + value
        return Box "data" ([byte[]]$inner)
    }

    # --- Helper: iTunes tag (e.g. ©nam, ©ART) wrapping a data atom ---
    function ItunesTag([string]$name, [string]$value) {
        $data = DataAtom $value
        return Box $name ([byte[]]$data)
    }

    # --- Build ilst (iTunes metadata container) ---
    $ilstPayload = @()
    if ($Book.Title)    { $ilstPayload += ItunesTag ([char]0x00A9 + "nam") $Book.Title }
    if ($Book.Author)   { $ilstPayload += ItunesTag ([char]0x00A9 + "ART") $Book.Author }
    if ($Book.Title)    { $ilstPayload += ItunesTag ([char]0x00A9 + "alb") $Book.Title }  # album = book title
    if ($Book.Year)     { $ilstPayload += ItunesTag ([char]0x00A9 + "day") $Book.Year }
    if ($Book.Genre)    { $ilstPayload += ItunesTag ([char]0x00A9 + "gen") $Book.Genre }
    if ($Book.Narrator) { $ilstPayload += ItunesTag ([char]0x00A9 + "wrt") $Book.Narrator }  # composer/writer field for narrator

    # ASIN as freeform atom: ----/mean/name/data
    if ($Book.Asin) {
        $meanPayload = (BE32 0) + $enc.GetBytes("com.apple.iTunes")
        $meanBox = Box "mean" ([byte[]]$meanPayload)
        $namePayload = (BE32 0) + $enc.GetBytes("ASIN")
        $nameBox = Box "name" ([byte[]]$namePayload)
        $dataBox = DataAtom $Book.Asin
        $freeformPayload = [byte[]]$meanBox + [byte[]]$nameBox + [byte[]]$dataBox
        $ilstPayload += Box "----" ([byte[]]$freeformPayload)
    }

    $ilst = Box "ilst" ([byte[]]$ilstPayload)

    # --- meta atom: hdlr + ilst ---
    $metaHdlrPayload = @([byte]0) * 4 +    # version + flags
                        @([byte]0) * 4 +    # pre_defined
                        $enc.GetBytes("mdir") +  # handler type
                        $enc.GetBytes("appl") +  # reserved (12 bytes)
                        @([byte]0) * 8 +
                        @([byte]0)          # name (null terminator)
    $metaHdlr = Box "hdlr" ([byte[]]$metaHdlrPayload)
    # meta is a full box (version 0, flags 0)
    $metaPayload = (BE32 0) + [byte[]]$metaHdlr + [byte[]]$ilst
    $meta = Box "meta" ([byte[]]$metaPayload)

    # --- udta ---
    $udta = Box "udta" ([byte[]]$meta)

    # --- mvhd (movie header, version 0, 108 bytes total) ---
    $mvhdPayload = @([byte]0) * 4 +    # version + flags
                   (BE32 0) +           # creation_time
                   (BE32 0) +           # modification_time
                   (BE32 44100) +       # timescale
                   (BE32 44100) +       # duration (1 second)
                   @([byte]0,1,0,0) +   # rate = 1.0 (fixed-point 16.16)
                   @([byte]1,0) +       # volume = 1.0 (fixed-point 8.8)
                   @([byte]0) * 10 +    # reserved
                   # 3x3 unity matrix (36 bytes)
                   (BE32 0x00010000) + @([byte]0)*4 + @([byte]0)*4 +
                   @([byte]0)*4 + (BE32 0x00010000) + @([byte]0)*4 +
                   @([byte]0)*4 + @([byte]0)*4 + (BE32 0x40000000) +
                   @([byte]0) * 24 +    # pre_defined
                   (BE32 2)             # next_track_id
    $mvhd = Box "mvhd" ([byte[]]$mvhdPayload)

    # --- tkhd (track header) ---
    $tkhdPayload = @([byte]0) +         # version
                   @([byte]0,0,3) +     # flags (enabled + in_movie)
                   (BE32 0) +           # creation_time
                   (BE32 0) +           # modification_time
                   (BE32 1) +           # track_id
                   @([byte]0) * 4 +     # reserved
                   (BE32 44100) +       # duration
                   @([byte]0) * 8 +     # reserved
                   @([byte]0,0) +       # layer
                   @([byte]0,0) +       # alternate_group
                   @([byte]1,0) +       # volume = 1.0
                   @([byte]0,0) +       # reserved
                   # 3x3 unity matrix (36 bytes)
                   (BE32 0x00010000) + @([byte]0)*4 + @([byte]0)*4 +
                   @([byte]0)*4 + (BE32 0x00010000) + @([byte]0)*4 +
                   @([byte]0)*4 + @([byte]0)*4 + (BE32 0x40000000) +
                   (BE32 0) +           # width
                   (BE32 0)             # height
    $tkhd = Box "tkhd" ([byte[]]$tkhdPayload)

    # --- mdhd (media header) ---
    $mdhdPayload = @([byte]0) * 4 +     # version + flags
                   (BE32 0) +           # creation_time
                   (BE32 0) +           # modification_time
                   (BE32 44100) +       # timescale
                   (BE32 44100) +       # duration
                   @([byte]0x55,[byte]0xC4) +  # language = "und"
                   @([byte]0,0)         # pre_defined
    $mdhd = Box "mdhd" ([byte[]]$mdhdPayload)

    # --- hdlr (handler, audio) ---
    $hdlrName = $enc.GetBytes("SoundHandler") + @([byte]0)
    $hdlrPayload = @([byte]0) * 4 +     # version + flags
                   @([byte]0) * 4 +     # pre_defined
                   $enc.GetBytes("soun") +  # handler_type
                   @([byte]0) * 12 +    # reserved
                   $hdlrName
    $hdlr = Box "hdlr" ([byte[]]$hdlrPayload)

    # --- smhd ---
    $smhd = Box "smhd" ([byte[]](@([byte]0)*4 + @([byte]0)*2 + @([byte]0)*2))

    # --- dref ---
    $urlEntry = Box "url " ([byte[]](@([byte]0,0,0,1)))  # self-contained
    $drefPayload = @([byte]0) * 4 + (BE32 1) + [byte[]]$urlEntry  # version+flags, entry_count=1
    $dref = Box "dref" ([byte[]]$drefPayload)
    $dinf = Box "dinf" ([byte[]]$dref)

    # --- stbl (empty sample tables) ---
    $stsd = Box "stsd" ([byte[]](@([byte]0)*4 + (BE32 0)))    # version+flags, entry_count=0
    $stts = Box "stts" ([byte[]](@([byte]0)*4 + (BE32 0)))
    $stsc = Box "stsc" ([byte[]](@([byte]0)*4 + (BE32 0)))
    $stsz = Box "stsz" ([byte[]](@([byte]0)*4 + (BE32 0) + (BE32 0)))  # sample_size=0, sample_count=0
    $stco = Box "stco" ([byte[]](@([byte]0)*4 + (BE32 0)))
    $stbl = Box "stbl" ([byte[]]([byte[]]$stsd + [byte[]]$stts + [byte[]]$stsc + [byte[]]$stsz + [byte[]]$stco))

    # --- minf ---
    $minf = Box "minf" ([byte[]]([byte[]]$smhd + [byte[]]$dinf + [byte[]]$stbl))

    # --- mdia ---
    $mdia = Box "mdia" ([byte[]]([byte[]]$mdhd + [byte[]]$hdlr + [byte[]]$minf))

    # --- trak ---
    $trak = Box "trak" ([byte[]]([byte[]]$tkhd + [byte[]]$mdia))

    # --- moov ---
    $moov = Box "moov" ([byte[]]([byte[]]$mvhd + [byte[]]$trak + [byte[]]$udta))

    # --- ftyp ---
    $ftypPayload = $enc.GetBytes("M4B ") +   # major brand
                   (BE32 0) +                 # minor version
                   $enc.GetBytes("M4B ") +    # compatible brand
                   $enc.GetBytes("isom")      # compatible brand
    $ftyp = Box "ftyp" ([byte[]]$ftypPayload)

    # --- mdat (empty) ---
    $mdat = Box "mdat" ([byte[]]@())

    # --- Write the file ---
    $allBytes = [byte[]]$ftyp + [byte[]]$moov + [byte[]]$mdat
    [System.IO.File]::WriteAllBytes($OutputPath, $allBytes)
}

function New-BareM4b {
    param([string]$OutputPath)
    # Create a minimal M4B with valid MP4 structure but no metadata.
    # Reuse New-SyntheticM4b with empty fields.
    $bare = [pscustomobject]@{Title=""; Author=""; Year=""; Series=""; Pos=""; Isbn=""; Genre=""; Language="en"; Narrator=""; Asin=""}
    New-SyntheticM4b -Book $bare -OutputPath $OutputPath
}

function Get-SafeFilename {
    param([string]$Title, [string]$Format = "Epub")
    $safe = $Title -replace '[\\/:*?"<>|]', '_'
    $ext = if ($Format -eq "Audiobook") { ".m4b" } else { ".epub" }
    return "$safe$ext"
}

# ===========================================================================
# MAIN
# ===========================================================================

$RunStart = Get-Date

Write-H "TUVIMA MEDIA INGESTION TEST"
Write-RL " Run started : $($RunStart.ToString('yyyy-MM-dd HH:mm:ss'))"
Write-RL " Engine URL  : $EngineUrl"
Write-RL " Sample size : $Count of $($Catalog.Count) catalog entries"
Write-RL " Seed        : $(if ($Seed -ge 0) { $Seed } else { 'random' })"
Write-RL ""

# -- 1. Engine health check --------------------------------------------------
Write-R " Checking engine..." -c "Gray"
$status       = Invoke-Api "/system/status"
$engineWasUp  = $null -ne $status
$coreSettings = $null
if ($engineWasUp) {
    Write-R " Engine online" -c "Green"
    # -- 2. Resolve the Books library source from engine settings
    if (-not $WatchDirectory) {
        $coreSettings = Invoke-Api "/settings/libraries"
        $resolvedWatchDirectory = Resolve-BookLibrarySourceFromSettings $coreSettings
        if ($resolvedWatchDirectory) {
            $WatchDirectory = $resolvedWatchDirectory
        }
    }
} elseif ($doWipe) {
    Write-R " Engine offline - will wipe and restart." -c "Yellow"
} else {
    Write-R " ENGINE NOT RESPONDING at $EngineUrl" -c "Red"
    Write-R "   Start the engine first: cd src/MediaEngine.Api && dotnet run" -c "Yellow"
    exit 1
}

if (-not $doWipe) {
    if (-not $WatchDirectory -or -not (Test-Path $WatchDirectory)) {
        Write-R " Watch directory not found: '$WatchDirectory'" -c "Red"
        Write-R "   Set -WatchDirectory or configure it in the engine settings." -c "Yellow"
        exit 1
    }
    Write-R " Watch dir   : $WatchDirectory" -c "Green"
}

# -- 3. Resolve DB and library paths -----------------------------------------
$ApiDbPath   = Join-Path $RepoRoot "src\MediaEngine.Api\library.db"
$LibraryRoot = ""
if ($null -ne $coreSettings -and $coreSettings.library_root) {
    $LibraryRoot = $coreSettings.library_root
}
# Fallback: read the library root and Books source from configuration when the Engine is offline.
if (-not $LibraryRoot -or (-not $WatchDirectory)) {
    $configDir = Join-Path $RepoRoot "config"
    $coreJson  = Join-Path $configDir "core.json"
    if (Test-Path $coreJson) {
        try {
            $coreFile = Get-Content $coreJson -Raw | ConvertFrom-Json
            if (-not $LibraryRoot -and $coreFile.library_root) {
                $LibraryRoot = $coreFile.library_root
                Write-R " Library root (from config): $LibraryRoot" -c "DarkYellow"
            }
            if (-not $WatchDirectory) {
                $librariesJson = Join-Path $configDir "libraries.json"
                $librariesFile = if (Test-Path $librariesJson) { Get-Content $librariesJson -Raw | ConvertFrom-Json } else { $null }
                $resolvedWatchDirectory = Resolve-BookLibrarySourceFromSettings $librariesFile
                if ($resolvedWatchDirectory) {
                    $WatchDirectory = $resolvedWatchDirectory
                }
            }
            if ($WatchDirectory) {
                Write-R " Watch dir (from config): $WatchDirectory" -c "DarkYellow"
            }
        } catch {
            Write-R " Warning: could not parse config/core.json" -c "DarkYellow"
        }
    }
}

# -- 4. Wipe (default unless -NoWipe) ----------------------------------------
if ($doWipe) {
    Write-R " Auto-wiping database and library (use -NoWipe to skip)..." -c "Yellow"

    # Stop ALL running engine instances (any dotnet.exe running MediaEngine.Api)
    $enginePort = ([uri]$EngineUrl).Port
    $apiProcs   = Get-CimInstance Win32_Process -Filter "name='dotnet.exe'" -ErrorAction SilentlyContinue |
                  Where-Object { $_.CommandLine -like "*MediaEngine.Api*" }
    $tcpConn    = Get-NetTCPConnection -LocalPort $enginePort -State Listen -ErrorAction SilentlyContinue
    if ($tcpConn -and -not $apiProcs) {
        $apiProcs = @([PSCustomObject]@{ ProcessId = $tcpConn.OwningProcess })
    }
    if ($apiProcs) {
        Write-R " Stopping engine instances..." -c "Yellow"
        foreach ($p in $apiProcs) { Stop-Process -Id $p.ProcessId -Force -ErrorAction SilentlyContinue }
        Start-Sleep -Seconds 3   # wait for DLL locks to release
        Write-R "   Engine stopped" -c "DarkYellow"
    }

    Write-R " Wiping database and library..." -c "Yellow"
    if (Test-Path $ApiDbPath) {
        Remove-Item $ApiDbPath -Force -ErrorAction SilentlyContinue
        Write-R "   Deleted: $ApiDbPath" -c "DarkYellow"
    }
    $bak = "$ApiDbPath.bak"
    if (Test-Path $bak) { Remove-Item $bak -Force }
    foreach ($ext in @(".wal", ".shm")) {
        $f = "$ApiDbPath$ext"
        if (Test-Path $f) { Remove-Item $f -Force -ErrorAction SilentlyContinue }
    }
    if ($LibraryRoot -and (Test-Path $LibraryRoot)) {
        Remove-Item -Path $LibraryRoot -Recurse -Force -ErrorAction SilentlyContinue
        Write-R "   Cleared: $LibraryRoot" -c "DarkYellow"
    }
    Write-R " Wipe complete." -c "Green"

    # Restart the engine and wait for it to come back online
    $apiDir = Join-Path $RepoRoot "src\MediaEngine.Api"
    Write-R " Restarting engine..." -c "Yellow"
    # Use --no-build (binaries already compiled) and force the correct port via env var
    $env:ASPNETCORE_URLS        = $EngineUrl
    $env:ASPNETCORE_ENVIRONMENT = "Development"
    $engineLog = Join-Path ([System.IO.Path]::GetTempPath()) "tuvima_engine_restart.log"
    $script:EngineProcess = Start-Process -FilePath "dotnet" -ArgumentList "run --no-build --no-launch-profile" `
        -WorkingDirectory $apiDir -NoNewWindow -RedirectStandardOutput $engineLog -RedirectStandardError "$engineLog.err" -PassThru
    $restartDeadline = (Get-Date).AddSeconds(60)
    $restarted = $false
    do {
        Start-Sleep -Seconds 3
        try {
            $ping = Invoke-RestMethod -Uri "$EngineUrl/system/status" -TimeoutSec 3 -ErrorAction Stop
            if ($ping) { $restarted = $true; break }
        } catch { }
    } while ((Get-Date) -lt $restartDeadline)
    if ($restarted) {
        Write-R " Engine back online." -c "Green"
        # Re-fetch settings after restart (fresh DB, fresh config)
        $coreSettings = Invoke-Api "/settings/libraries"
        if ($coreSettings -and $coreSettings.library_root) { $LibraryRoot = $coreSettings.library_root }
        if (-not $WatchDirectory) {
            $resolvedWatchDirectory = Resolve-BookLibrarySourceFromSettings $coreSettings
            if ($resolvedWatchDirectory) {
                $WatchDirectory = $resolvedWatchDirectory
            }
        }
    } else {
        Write-R " Engine did not restart within 60s. Check the engine manually." -c "Red"
        exit 1
    }
}

# Validate watch directory (after potential WipeFirst restart)
if (-not $WatchDirectory -or -not (Test-Path $WatchDirectory -PathType Container)) {
    if ($WatchDirectory -and -not (Test-Path $WatchDirectory)) {
        New-Item -ItemType Directory -Path $WatchDirectory -Force | Out-Null
        Write-R " Created watch directory: $WatchDirectory" -c "DarkYellow"
    } elseif (-not $WatchDirectory) {
        Write-R " Watch directory not found. Set -WatchDirectory or configure it in the engine." -c "Red"
        exit 1
    }
}
Write-R " Watch dir   : $WatchDirectory" -c "Green"

# -- 5. Stratified sampling --------------------------------------------------
# Proportions: 45% high, 15% audiobook, 20% medium, 10% low, 5% corrupt, 5% duplicate
# Foreign-language books (ForeignLanguage=$true) capped at max 2 per run.
if ($Seed -ge 0) {
    $rng = New-Object System.Random($Seed)
} else {
    $rng = New-Object System.Random
    $Seed = $rng.Next()
}

$nAudiobook = [Math]::Floor($Count * 0.15)
$nHigh      = [Math]::Floor($Count * 0.45)
$nMedium    = [Math]::Floor($Count * 0.20)
$nLow       = [Math]::Floor($Count * 0.10)
$nCorrupt   = [Math]::Floor($Count * 0.05)
$nDuplicate = $Count - $nHigh - $nAudiobook - $nMedium - $nLow - $nCorrupt

# Buckets from catalog
$bucketHigh      = @($Catalog | Where-Object { $_.S -eq "high" }      | Sort-Object { $rng.NextDouble() })
$bucketMedium    = @($Catalog | Where-Object { $_.S -eq "medium" }    | Sort-Object { $rng.NextDouble() })
$bucketLow       = @($Catalog | Where-Object { $_.S -eq "low" }       | Sort-Object { $rng.NextDouble() })
$bucketCorrupt   = @($Catalog | Where-Object { $_.S -eq "corrupt" }   | Sort-Object { $rng.NextDouble() })
$bucketAudiobook = @($Catalog | Where-Object { $_.S -eq "audiobook" } | Sort-Object { $rng.NextDouble() })
$bucketDuplicate = @($Catalog | Where-Object { $_.S -eq "duplicate" } | Sort-Object { $rng.NextDouble() })

# Medium bucket: cap foreign-language books at max 2
$foreignCap  = 2
$medForeign  = @($bucketMedium | Where-Object { $_.ForeignLanguage -eq $true })
$medDomestic = @($bucketMedium | Where-Object { $_.ForeignLanguage -ne $true })
$foreignTake = [Math]::Min($foreignCap, $medForeign.Count)
$domesticNeed = [Math]::Max(0, $nMedium - $foreignTake)
$domesticTake = [Math]::Min($domesticNeed, $medDomestic.Count)
# If domestic pool is short, fill remaining slots from foreign (still capped at foreignCap)
if ($domesticTake -lt $domesticNeed) { $foreignTake = [Math]::Min($foreignCap, $medForeign.Count) }
$mediumSelected = @($medDomestic | Select-Object -First $domesticTake) + @($medForeign | Select-Object -First $foreignTake)
# Shuffle the merged medium selection
$mediumSelected = @($mediumSelected | Sort-Object { $rng.NextDouble() }) | Select-Object -First $nMedium

$selected = @(
    ($bucketHigh      | Select-Object -First $nHigh)
    ($bucketAudiobook | Select-Object -First $nAudiobook)
    $mediumSelected
    ($bucketLow       | Select-Object -First $nLow)
    ($bucketCorrupt   | Select-Object -First $nCorrupt)
    ($bucketDuplicate | Select-Object -First $nDuplicate)
) | Where-Object { $_ -ne $null }

# Ensure duplicates have their originals in the list
foreach ($dup in ($selected | Where-Object { $_.S -eq "duplicate" })) {
    $origId = $dup.DuplicateOf
    if (-not ($selected | Where-Object { $_.Id -eq $origId })) {
        $orig = $Catalog | Where-Object { $_.Id -eq $origId }
        if ($orig) { $selected = @($selected) + @($orig) }
    }
}

# Ensure audiobooks with paired ebooks have their ebook counterpart in the list
foreach ($ab in ($selected | Where-Object { $_.S -eq "audiobook" -and $_.PairedWith })) {
    $pairedId = $ab.PairedWith
    if (-not ($selected | Where-Object { $_.Id -eq $pairedId })) {
        $paired = $Catalog | Where-Object { $_.Id -eq $pairedId }
        if ($paired) { $selected = @($selected) + @($paired) }
    }
}

# Sort: non-duplicates first (ensures originals ingested before duplicates)
$selected = @($selected | Sort-Object @{Expression={if($_.S -eq "duplicate"){1}else{0}}}, @{Expression={$_.Id}})

# -- 6. Generate EPUBs in temp dir -------------------------------------------
$TempDir = Join-Path ([System.IO.Path]::GetTempPath()) "tuvima_booktest_$Timestamp"
New-Item -ItemType Directory -Path $TempDir | Out-Null

$generatedPaths = @{}
$droppedFiles   = New-Object System.Collections.Generic.List[object]

foreach ($book in $selected) {
    $safeId   = $book.Id.ToString().PadLeft(3, '0')
    $safeName = Get-SafeFilename -Title $book.Title -Format $book.Format
    $filename = "${safeId}_${safeName}"
    $tmpPath  = Join-Path $TempDir $filename

    switch ($book.S) {
        "high" {
            New-ValidEpub -Book $book -OutputPath $tmpPath
        }
        "audiobook" {
            New-SyntheticM4b -Book $book -OutputPath $tmpPath
        }
        "medium" {
            $partial = [pscustomobject]@{Title=$book.Title; Author=$book.Author; Year=$book.Year; Series=""; Pos=""; Isbn=""; Genre=""; Language=$book.Language}
            New-ValidEpub -Book $partial -OutputPath $tmpPath
        }
        "low" {
            New-BareEpub -OutputPath $tmpPath
        }
        "corrupt" {
            $bytes = New-Object byte[] 512
            $rng.NextBytes($bytes)
            [System.IO.File]::WriteAllBytes($tmpPath, $bytes)
        }
        "duplicate" {
            $origPath = $generatedPaths[$book.DuplicateOf]
            if ($origPath -and (Test-Path $origPath)) {
                Copy-Item $origPath $tmpPath
            } else {
                New-ValidEpub -Book $book -OutputPath $tmpPath
            }
        }
    }

    $generatedPaths[$book.Id] = $tmpPath

    $meta = switch ($book.S) {
        "high"      { "title, author, year, ISBN$(if($book.Series){', series'})" }
        "audiobook" { "M4B: title, author, year$(if($book.Asin){', ASIN'})$(if($book.Series){', series'}) [narrator: $($book.Narrator)]" }
        "medium"    { "title, author, year" }
        "low"       { "filename only (no OPF)" }
        "corrupt"   { "(invalid bytes)" }
        "duplicate" { "identical to book #$($book.DuplicateOf)" }
        default     { "?" }
    }

    $droppedFiles.Add([pscustomobject]@{
        Num      = $droppedFiles.Count + 1
        Book     = $book
        TmpPath  = $tmpPath
        Filename = $filename
        Meta     = $meta
        Scenario = $book.S
    })
}

# -- 7. Pre-ingestion report -------------------------------------------------
Write-S "PRE-INGESTION: FILES TO DROP INTO WATCH FOLDER"

$hdr = " {0,-4} {1,-44} {2,-10} {3}" -f "#", "Filename", "Scenario", "Embedded Metadata"
$div = " {0,-4} {1,-44} {2,-10} {3}" -f "----", "--------------------------------------------", "----------", "-----------------------------"
Write-RL $hdr; Write-RL $div

foreach ($f in $droppedFiles) {
    $fn  = if ($f.Filename.Length -gt 44) { $f.Filename.Substring(0,41) + "..." } else { $f.Filename }
    $row = " {0,-4} {1,-44} {2,-10} {3}" -f $f.Num, $fn, $f.Scenario, $f.Meta
    $c   = switch ($f.Scenario) {
        "high"      { "Green" }
        "audiobook"  { "Magenta" }
        "medium"    { "Cyan" }
        "low"       { "Yellow" }
        "corrupt"   { "Red" }
        "duplicate" { "DarkCyan" }
        default     { "White" }
    }
    Write-R $row -c $c
}

$scenCounts = @($droppedFiles | Group-Object Scenario | ForEach-Object { "$($_.Count) $($_.Name)" })
Write-RL ""
Write-R " Total: $($droppedFiles.Count) files  |  $($scenCounts -join '  /  ')" -c "Gray"

# -- 8. Drop files -----------------------------------------------------------
Write-S "DROPPING FILES"
Write-R " Copying $($droppedFiles.Count) files to: $WatchDirectory" -c "Gray"

$DropStart = Get-Date

$wave1 = @($droppedFiles | Where-Object { $_.Scenario -ne "duplicate" })
$wave2 = @($droppedFiles | Where-Object { $_.Scenario -eq "duplicate" })

foreach ($f in $wave1) {
    Copy-Item $f.TmpPath (Join-Path $WatchDirectory $f.Filename) -Force
}

if ($wave2.Count -gt 0) {
    Write-R " Waiting 5s before dropping duplicates..." -c "DarkGray"
    Start-Sleep -Seconds 5
    foreach ($f in $wave2) {
        Copy-Item $f.TmpPath (Join-Path $WatchDirectory $f.Filename) -Force
    }
}

Write-R " All $($droppedFiles.Count) files dropped at $((Get-Date).ToString('HH:mm:ss'))" -c "Green"
Write-R " Waiting for engine (timeout: ${TimeoutSeconds}s)..." -c "Gray"

# -- 9. Poll for completion --------------------------------------------------
$expectedEvents = $droppedFiles.Count
$terminalTypes  = @("FileIngested", "DuplicateSkipped", "MediaFailed")
$deadline       = (Get-Date).AddSeconds($TimeoutSeconds)
$lastCount      = 0

do {
    Start-Sleep -Seconds 2
    $activity = Invoke-Api "/activity/recent?limit=500"
    if ($activity) {
        $sinceStr = $DropStart.AddSeconds(-2).ToString("O")
        $events   = @($activity | Where-Object { $_.action_type -in $terminalTypes -and $_.occurred_at -gt $sinceStr })
        $cnt      = $events.Count
        if ($cnt -ne $lastCount) {
            Write-R "   ... $cnt / $expectedEvents events detected" -c "DarkGray"
            $lastCount = $cnt
        }
        if ($cnt -ge $expectedEvents) { break }
    }
} while ((Get-Date) -lt $deadline)

$IngDuration = (Get-Date) - $DropStart

if ($lastCount -lt $expectedEvents) {
    Write-R " Timeout reached. $lastCount of $expectedEvents events detected." -c "Yellow"
} else {
    Write-R " Ingestion complete in $([int]$IngDuration.TotalSeconds)s" -c "Green"
}

# -- 9b. Wait for hydration pipeline to complete ----------------------------
# Items are invisible in the LibraryItem until hydration assigns a QID.
# Poll the LibraryItem count until it stabilizes (no new items for 15s) or timeout.
$HydrationStart = Get-Date
$hydrationTimeout = 180  # max 3 minutes for hydration
$hydrationDeadline = (Get-Date).AddSeconds($hydrationTimeout)
$stableCount = 0
$stableSince = Get-Date
$stableThreshold = 15  # seconds with no new items = done

Write-R " Waiting for hydration pipeline (QID assignment)..." -c "Gray"

do {
    Start-Sleep -Seconds 3
    $regCheck = Invoke-Api "/dev/library-items?limit=1"
    $currentCount = if ($regCheck -and $regCheck.total_count) { $regCheck.total_count } else { 0 }

    if ($currentCount -ne $stableCount) {
        $stableCount = $currentCount
        $stableSince = Get-Date
        Write-R "   ... $currentCount items identified" -c "DarkGray"
    }

    $stableFor = ((Get-Date) - $stableSince).TotalSeconds
    if ($stableFor -ge $stableThreshold -and $stableCount -gt 0) {
        break
    }
} while ((Get-Date) -lt $hydrationDeadline)

$HydrationDuration = (Get-Date) - $HydrationStart
Write-R " Hydration complete: $stableCount items identified in $([int]$HydrationDuration.TotalSeconds)s" -c "Green"

# -- 10. Query results -------------------------------------------------------
Start-Sleep -Seconds 2
$libraryItem    = Invoke-Api "/dev/library-items?limit=200"
$reviewItems = Invoke-Api "/review/pending?limit=200"
$actAll      = Invoke-Api "/activity/recent?limit=500"

$byFilename = @{}
$byTitle    = @{}
if ($libraryItem -and $libraryItem.items) {
    foreach ($item in $libraryItem.items) {
        if ($item.file_name) { $byFilename[$item.file_name] = $item }
        # Index by title — prefer the highest confidence entry when duplicates exist
        if ($item.title) {
            $key = $item.title.ToLowerInvariant()
            $existing = $byTitle[$key]
            if (-not $existing -or $item.confidence -gt $existing.confidence) {
                $byTitle[$key] = $item
            }
        }
    }
}

$sinceStr2   = $DropStart.AddSeconds(-2).ToString("O")
$dupSkips    = @()
$fails       = @()
if ($actAll) {
    $dupSkips = @($actAll | Where-Object { $_.action_type -eq "DuplicateSkipped" -and $_.occurred_at -gt $sinceStr2 })
    $fails    = @($actAll | Where-Object { $_.action_type -eq "MediaFailed"      -and $_.occurred_at -gt $sinceStr2 })
}

# -- 11. Post-ingestion report -----------------------------------------------
Write-S "POST-INGESTION: RESULTS"

$rhdr = " {0,-4} {1,-30} {2,-22} {3,-12} {4,-7} {5}" -f "#", "Title", "Author", "Status", "Conf", "Notes"
$rdiv = " {0,-4} {1,-30} {2,-22} {3,-12} {4,-7} {5}" -f "----", "------------------------------", "----------------------", "------------", "-------", "-----------------------"
Write-RL $rhdr; Write-RL $rdiv

$stats = @{ Staged=0; Review=0; Duplicate=0; Failed=0; Unknown=0; TotalConf=0.0; ConfCount=0 }

foreach ($f in $droppedFiles) {
    $status = "?"
    $conf   = "  ?"
    $notes  = ""
    $color  = "Gray"

    # For duplicates/corrupt: check activity events first (these never appear in libraryItem)
    $isDupInActivity = $false
    if ($f.Scenario -eq "duplicate") {
        $isDupInActivity = (@($dupSkips | Where-Object { $_.detail -like "*$($f.Filename)*" }).Count -gt 0)
        if (-not $isDupInActivity -and $f.Book.Title) {
            $isDupInActivity = (@($dupSkips | Where-Object { $_.detail -like "*$($f.Book.Title)*" }).Count -gt 0)
        }
    }
    $isFailInActivity = $false
    if ($f.Scenario -eq "corrupt") {
        $isFailInActivity = (@($fails | Where-Object { $_.detail -like "*$($f.Filename)*" }).Count -gt 0)
    }

    if ($isDupInActivity) {
        $status = "Duplicate"
        $conf   = "  -"
        $notes  = "hash match -> skip"
        $color  = "DarkCyan"
        $stats.Duplicate++
    } elseif ($isFailInActivity) {
        $status = "Failed"
        $conf   = "  -"
        $notes  = "corrupt/parse error"
        $color  = "Red"
        $stats.Failed++
    } else {
        $item = $byFilename[$f.Filename]
        if (-not $item -and $f.Book.Title) { $item = $byTitle[$f.Book.Title.ToLowerInvariant()] }

        if ($item) {
            $pct  = [int]($item.confidence * 100)
            $conf = "${pct}%"

            $rv = if ($item.review_item_id) { $item.review_item_id } else { $null }
            if ($rv) {
                $trigger = if ($item.review_trigger) { $item.review_trigger } else { "review" }
                $status = "Review"
                $notes  = $trigger
                $color  = "Yellow"
                $stats.Review++
            } elseif ($item.status) {
                $status = $item.status
                $notes  = if ($item.author) { "by $($item.author)" } else { "" }
                $color  = if ($pct -ge 70) { "Green" } else { "Cyan" }
                $stats.Staged++
            }
            $stats.TotalConf += $item.confidence
            $stats.ConfCount++

        } elseif ($f.Scenario -eq "duplicate") {
            $status = "Duplicate"
            $conf   = "  -"
            $notes  = "hash match -> skip"
            $color  = "DarkCyan"
            $stats.Duplicate++

        } elseif ($f.Scenario -eq "corrupt") {
            $status = "Failed"
            $conf   = "  -"
            $notes  = "corrupt file"
            $color  = "Red"
            $stats.Failed++

        } else {
            # Fallback: search the libraryItem by title (handles Wikidata language renames)
            if ($f.Book.Title -and $f.Book.Title.Length -gt 3) {
                $enc   = [System.Uri]::EscapeDataString($f.Book.Title)
                $srch  = Invoke-Api "/dev/library-items?search=$enc&limit=5"
                $sitem = if ($srch -and $srch.items -and $srch.items.Count -gt 0) { $srch.items[0] } else { $null }
                # Second fallback: search by author (handles Wikidata native-language title renames)
                if (-not $sitem -and $f.Book.Author -and $f.Book.Author.Length -gt 3) {
                    $encA  = [System.Uri]::EscapeDataString($f.Book.Author)
                    $srch2 = Invoke-Api "/dev/library-items?search=$encA&limit=10"
                    if ($srch2 -and $srch2.items -and $srch2.items.Count -gt 0) {
                        # Pick highest-confidence confirmed item for this author
                        $candidates = @($srch2.items | Where-Object { $_.author -and $_.author -like "*$($f.Book.Author.Split(' ')[-1])*" })
                        if ($candidates.Count -eq 0) { $candidates = @($srch2.items) }
                        $sitem = $candidates | Sort-Object confidence -Descending | Select-Object -First 1
                    }
                }
                if ($sitem) {
                    $pct  = [int]($sitem.confidence * 100)
                    $conf = "${pct}%"
                    $rv   = if ($sitem.review_item_id) { $sitem.review_item_id } else { $null }
                    if ($rv) {
                        $trigger = if ($sitem.review_trigger) { $sitem.review_trigger } else { "review" }
                        $status = "Review"
                        $notes  = "$trigger (title renamed: $($sitem.title))"
                        $color  = "Yellow"
                        $stats.Review++
                    } else {
                        $status = if ($sitem.status) { $sitem.status } else { "?" }
                        $notes  = "title renamed: $($sitem.title)"
                        $color  = if ($pct -ge 70) { "Green" } else { "Cyan" }
                        $stats.Staged++
                    }
                    $stats.TotalConf += $sitem.confidence
                    $stats.ConfCount++
                } else {
                    $status = "Not found"
                    $conf   = "  ?"
                    $notes  = "not in libraryItem"
                    $color  = "DarkYellow"
                    $stats.Unknown++
                }
            } else {
                $status = "Not found"
                $conf   = "  ?"
                $notes  = "not in libraryItem"
                $color  = "DarkYellow"
                $stats.Unknown++
            }
        }
    } # end else (not dup/fail activity)

    $td = if ($f.Book.Title.Length  -gt 30) { $f.Book.Title.Substring(0,27)  + "..." } else { $f.Book.Title  }
    $ad = if ($f.Book.Author.Length -gt 22) { $f.Book.Author.Substring(0,19) + "..." } else { $f.Book.Author }
    $row = " {0,-4} {1,-30} {2,-22} {3,-12} {4,-7} {5}" -f $f.Num, $td, $ad, $status, $conf, $notes
    Write-R $row -c $color
}

# -- 12. Summary -------------------------------------------------------------
Write-S "SUMMARY"

$avgConf = if ($stats.ConfCount -gt 0) { [int]($stats.TotalConf / $stats.ConfCount * 100) } else { 0 }
$total   = $droppedFiles.Count
$RunEnd  = Get-Date
$RunDur  = $RunEnd - $RunStart

$summaryData = @(
    @{ L=" Total files dropped";    V=$total;                    C="White" }
    @{ L=" [OK]  Staged";          V="$($stats.Staged) ($(if($total -gt 0){[int]($stats.Staged/$total*100)}else{0})%)";    C="Green" }
    @{ L=" [!]   Sent to review";  V="$($stats.Review) ($(if($total -gt 0){[int]($stats.Review/$total*100)}else{0})%)";    C="Yellow" }
    @{ L=" [DUP] Duplicates";      V="$($stats.Duplicate) ($(if($total -gt 0){[int]($stats.Duplicate/$total*100)}else{0})%)"; C="DarkCyan" }
    @{ L=" [ERR] Failed/corrupt";  V="$($stats.Failed) ($(if($total -gt 0){[int]($stats.Failed/$total*100)}else{0})%)";    C="Red" }
    @{ L=" [?]   Unresolved";      V=$stats.Unknown;             C="DarkYellow" }
    @{ L="";                       V="";                         C="White" }
    @{ L=" Avg confidence score";  V="${avgConf}%";              C="White" }
    @{ L=" Ingestion duration";    V="$([int]$IngDuration.TotalSeconds)s"; C="White" }
    @{ L=" Total run time";        V="$([int]$RunDur.TotalSeconds)s";      C="White" }
    @{ L=" Random seed used";      V=$Seed;                      C="White" }
)

foreach ($row in $summaryData) {
    if ($row.L) {
        $line = " {0,-26} : {1}" -f $row.L, $row.V
        Write-R $line -c $row.C
    } else {
        Write-RL ""
    }
}

# -- Processing Timeline -----------------------------------------------------
Write-S "PROCESSING TIMELINE"
Write-RL "  File generation     : $([int](($DropStart - $RunStart).TotalSeconds))s"
Write-RL "  File drop           : $([int](($DropStart - $RunStart).TotalSeconds))s - $((Get-Date $DropStart).ToString('HH:mm:ss'))"
Write-RL "  Ingestion (hashing) : $([int]$IngDuration.TotalSeconds)s"
Write-RL "  Hydration (QIDs)    : $([int]$HydrationDuration.TotalSeconds)s"
Write-RL "  Total pipeline      : $([int]((Get-Date) - $DropStart).TotalSeconds)s"
Write-RL ""

# Rate analysis
$uniqueFiles = @($droppedFiles | Where-Object { $_.Scenario -notin @("duplicate","corrupt") }).Count
if ($HydrationDuration.TotalSeconds -gt 0 -and $stableCount -gt 0) {
    $qidsPerSec = [Math]::Round($stableCount / $HydrationDuration.TotalSeconds, 2)
    $avgSecsPerQid = [Math]::Round($HydrationDuration.TotalSeconds / $stableCount, 2)
    Write-RL "  Hydration rate      : $qidsPerSec QIDs/sec ($avgSecsPerQid sec/QID)"
}
Write-RL "  Identified / Total  : $stableCount / $uniqueFiles ($([int](100.0 * $stableCount / [Math]::Max(1,$uniqueFiles)))%)"

# -- 13. Review queue detail -------------------------------------------------
if ($stats.Review -gt 0 -and $reviewItems) {
    $rvList = @($reviewItems | Select-Object -First 20)
    if ($rvList.Count -gt 0) {
        Write-S "REVIEW QUEUE ITEMS"
        foreach ($rv in $rvList) {
            $trigger = if ($rv.review_trigger) { $rv.review_trigger } else { "unknown" }
            $conf2   = if ($rv.confidence)     { "$([int]($rv.confidence * 100))%" } else { "?" }
            $eid     = if ($rv.entity_id)      { $rv.entity_id.ToString().Substring(0,8) } else { "?" }
            Write-R "  [$eid...]  trigger=$trigger  conf=$conf2  $($rv.detail)" -c "Yellow"
        }
    }
}

# -- 14. Save report ---------------------------------------------------------
$ReportLines.Add("")
$ReportLines.Add(" Report saved : $ReportPath")
$ReportLines.Add(" Run ended    : $($RunEnd.ToString('yyyy-MM-dd HH:mm:ss'))")

[System.IO.File]::WriteAllLines($ReportPath, $ReportLines, [System.Text.Encoding]::UTF8)

Write-RL ""
Write-R " Report saved to: $ReportPath" -c "DarkGray"
Write-RL ""

# -- 15. Cleanup -------------------------------------------------------------
Remove-Item -Path $TempDir -Recurse -Force -ErrorAction SilentlyContinue

# Stop the engine instance started by this script (wipe+restart path only)
if ($script:EngineProcess -and -not $script:EngineProcess.HasExited) {
    Write-R " Stopping engine (started by this script)..." -c "DarkGray"
    Stop-Process -Id $script:EngineProcess.Id -Force -ErrorAction SilentlyContinue
    Write-R " Engine stopped." -c "DarkGray"
}
