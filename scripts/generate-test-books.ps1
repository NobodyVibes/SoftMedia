# Generates a small test library of synthesized books for SoftMedia.
# Output: <repo>/test-fixtures/books/{cbz,pdf,epub}/
# Run: pwsh -File scripts/generate-test-books.ps1

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

$root = Split-Path -Parent $PSScriptRoot
$out  = Join-Path $root 'test-fixtures\books'
New-Item -ItemType Directory -Force -Path $out | Out-Null

# ---------------------------------------------------------------- CBZ ----------
function New-Cbz {
    param(
        [string]$Path, [string]$Title, [int]$Pages,
        [System.Drawing.Color[]]$Palette,
        [bool]$UnpaddedNames = $false,    # When true, name pages page1.png..pageN.png to exercise natural-sort
        [string]$Series = $null,          # ComicInfo.xml fields (optional)
        [int]$IssueNumber = 0,
        [int]$Year = 0,
        [string]$Publisher = $null,
        [string]$Writer = $null,
        [string]$Genre = $null,
        [string]$Summary = $null,
        [bool]$IncludeComicInfo = $true
    )

    $tmp = Join-Path ([System.IO.Path]::GetTempPath()) ([Guid]::NewGuid())
    New-Item -ItemType Directory -Force -Path $tmp | Out-Null
    try {
        for ($i = 1; $i -le $Pages; $i++) {
            $bmp = New-Object System.Drawing.Bitmap 800, 1200
            $g   = [System.Drawing.Graphics]::FromImage($bmp)
            $g.SmoothingMode = 'AntiAlias'
            $g.TextRenderingHint = 'AntiAlias'

            $color = $Palette[($i - 1) % $Palette.Length]
            $g.Clear($color)

            $titleFont = New-Object System.Drawing.Font 'Arial', 36, ([System.Drawing.FontStyle]::Bold)
            $pageFont  = New-Object System.Drawing.Font 'Arial', 96, ([System.Drawing.FontStyle]::Bold)
            $sf = New-Object System.Drawing.StringFormat
            $sf.Alignment = 'Center'
            $sf.LineAlignment = 'Center'

            $g.DrawString($Title, $titleFont, [System.Drawing.Brushes]::White,
                (New-Object System.Drawing.RectangleF 0, 100, 800, 100), $sf)
            $g.DrawString("Page $i of $Pages", $pageFont, [System.Drawing.Brushes]::White,
                (New-Object System.Drawing.RectangleF 0, 500, 800, 200), $sf)

            $fileName = if ($UnpaddedNames) { "page$i.png" } else { "page{0:D3}.png" -f $i }
            $bmp.Save((Join-Path $tmp $fileName),
                [System.Drawing.Imaging.ImageFormat]::Png)
            $g.Dispose(); $bmp.Dispose()
        }

        # Emit a ComicInfo.xml per the Anansi spec so the ComicInfoXmlProvider
        # has real data to extract during end-to-end testing.
        if ($IncludeComicInfo -and $Series) {
            $comicInfoXml = Format-ComicInfoXml `
                -Title $Title -Series $Series -IssueNumber $IssueNumber `
                -Year $Year -Publisher $Publisher -Writer $Writer -Genre $Genre `
                -Summary $Summary -PageCount $Pages
            Set-Content -Path (Join-Path $tmp 'ComicInfo.xml') -Value $comicInfoXml -Encoding UTF8 -NoNewline
        }

        if (Test-Path $Path) { Remove-Item $Path -Force }
        [System.IO.Compression.ZipFile]::CreateFromDirectory(
            $tmp, $Path, [System.IO.Compression.CompressionLevel]::Optimal, $false)
    } finally {
        Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
    }
}

function Format-ComicInfoXml {
    param(
        [string]$Title, [string]$Series, [int]$IssueNumber, [int]$Year,
        [string]$Publisher, [string]$Writer, [string]$Genre, [string]$Summary,
        [int]$PageCount
    )

    function ConvertTo-XmlEscaped([string]$s) {
        if ($null -eq $s) { return '' }
        return $s.Replace('&', '&amp;').Replace('<', '&lt;').Replace('>', '&gt;')
    }

    $elements = New-Object System.Collections.Generic.List[string]
    if ($Title)           { $elements.Add("  <Title>$(ConvertTo-XmlEscaped $Title)</Title>") }
    if ($Series)          { $elements.Add("  <Series>$(ConvertTo-XmlEscaped $Series)</Series>") }
    if ($IssueNumber -gt 0) { $elements.Add("  <Number>$IssueNumber</Number>") }
    if ($Year -gt 0)      { $elements.Add("  <Year>$Year</Year>") }
    if ($Publisher)       { $elements.Add("  <Publisher>$(ConvertTo-XmlEscaped $Publisher)</Publisher>") }
    if ($Writer)          { $elements.Add("  <Writer>$(ConvertTo-XmlEscaped $Writer)</Writer>") }
    if ($Genre)           { $elements.Add("  <Genre>$(ConvertTo-XmlEscaped $Genre)</Genre>") }
    if ($Summary)         { $elements.Add("  <Summary>$(ConvertTo-XmlEscaped $Summary)</Summary>") }
    if ($PageCount -gt 0) { $elements.Add("  <PageCount>$PageCount</PageCount>") }

    return @"
<?xml version="1.0" encoding="utf-8"?>
<ComicInfo xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema">
$($elements -join "`n")
</ComicInfo>
"@
}

# ---------------------------------------------------------------- EPUB ---------
# Cached CRC-32 table (IEEE 802.3 polynomial, reversed = 0xEDB88320).
$script:Crc32Table = $null
function Get-Crc32 {
    param([byte[]]$Data)
    # PowerShell 5.1 parses bare hex as signed Int32 (so 0xEDB88320 becomes negative).
    # Use decimal literals — these are parsed wide enough to fit and cast cleanly.
    $POLY    = [uint32]3987671840   # 0xEDB88320 (CRC-32 reversed polynomial)
    $ALL_ONE = [uint32]::MaxValue   # 0xFFFFFFFF

    if ($null -eq $script:Crc32Table) {
        $script:Crc32Table = New-Object 'uint32[]' 256
        for ($i = 0; $i -lt 256; $i++) {
            $c = [uint32]$i
            for ($j = 0; $j -lt 8; $j++) {
                if (($c -band 1) -ne 0) { $c = [uint32](($c -shr 1) -bxor $POLY) }
                else                    { $c = [uint32]($c -shr 1) }
            }
            $script:Crc32Table[$i] = $c
        }
    }
    $crc = $ALL_ONE
    foreach ($b in $Data) {
        $idx = ($crc -bxor $b) -band 0xFF
        $crc = [uint32](($crc -shr 8) -bxor $script:Crc32Table[$idx])
    }
    return [uint32]($crc -bxor $ALL_ONE)
}

function Compress-Deflate {
    param([byte[]]$Data)
    $ms  = New-Object System.IO.MemoryStream
    $def = New-Object System.IO.Compression.DeflateStream($ms, [System.IO.Compression.CompressionLevel]::Optimal, $true)
    $def.Write($Data, 0, $Data.Length)
    $def.Dispose()
    $bytes = $ms.ToArray()
    $ms.Dispose()
    return $bytes
}

# Writes a zip with full control over per-entry compression method.
# $Entries is an array of hashtables: @{ Name='...'; Data=[byte[]]; Stored=$true|$false }
function Write-Zip {
    param([string]$Path, [hashtable[]]$Entries)

    if (Test-Path $Path) { Remove-Item $Path -Force }
    $stream = [System.IO.File]::Create($Path)
    $bw     = New-Object System.IO.BinaryWriter($stream)
    $cdRecords = New-Object System.Collections.Generic.List[hashtable]

    # Helper: write a byte[] using the unambiguous 3-arg Stream.Write overload.
    # PowerShell's overload resolution sometimes picks BinaryWriter.Write(Byte) for arrays.
    function Write-Bytes([byte[]]$Buffer) {
        if ($Buffer.Length -gt 0) { $stream.Write($Buffer, 0, $Buffer.Length) }
    }

    foreach ($e in $Entries) {
        $name      = $e.Name
        $data      = [byte[]]$e.Data
        $stored    = [bool]$e.Stored
        $method    = if ($stored) { [uint16]0 } else { [uint16]8 }
        $crc       = Get-Crc32 $data
        $payload   = if ($stored) { $data } else { [byte[]](Compress-Deflate $data) }
        $nameBytes = [System.Text.Encoding]::UTF8.GetBytes($name)
        $offset    = $stream.Position

        # Local file header
        $bw.Write([uint32]0x04034b50)
        $bw.Write([uint16]20); $bw.Write([uint16]0); $bw.Write($method)
        $bw.Write([uint16]0);  $bw.Write([uint16]0x21)
        $bw.Write([uint32]$crc)
        $bw.Write([uint32]$payload.Length)
        $bw.Write([uint32]$data.Length)
        $bw.Write([uint16]$nameBytes.Length); $bw.Write([uint16]0)
        $bw.Flush()
        Write-Bytes $nameBytes
        Write-Bytes $payload

        $cdRecords.Add(@{
            Name = $nameBytes; Method = $method; Crc = $crc
            Csize = $payload.Length; Usize = $data.Length; Offset = $offset
        })
    }

    $cdStart = $stream.Position
    foreach ($r in $cdRecords) {
        $bw.Write([uint32]0x02014b50)
        $bw.Write([uint16]20); $bw.Write([uint16]20)
        $bw.Write([uint16]0);  $bw.Write($r.Method)
        $bw.Write([uint16]0);  $bw.Write([uint16]0x21)
        $bw.Write([uint32]$r.Crc)
        $bw.Write([uint32]$r.Csize); $bw.Write([uint32]$r.Usize)
        $bw.Write([uint16]$r.Name.Length)
        $bw.Write([uint16]0); $bw.Write([uint16]0)
        $bw.Write([uint16]0); $bw.Write([uint16]0)
        $bw.Write([uint32]0)
        $bw.Write([uint32]$r.Offset)
        $bw.Flush()
        Write-Bytes $r.Name
    }
    $cdSize = $stream.Position - $cdStart

    # End of central directory record
    $bw.Write([uint32]0x06054b50)
    $bw.Write([uint16]0); $bw.Write([uint16]0)
    $bw.Write([uint16]$cdRecords.Count); $bw.Write([uint16]$cdRecords.Count)
    $bw.Write([uint32]$cdSize); $bw.Write([uint32]$cdStart)
    $bw.Write([uint16]0)

    $bw.Dispose(); $stream.Dispose()
}

function New-Epub {
    param([string]$Path, [string]$Title, [string]$Author, [string[]]$Chapters)

    $bookId  = [Guid]::NewGuid().ToString()
    $entries = New-Object System.Collections.Generic.List[hashtable]

    # mimetype MUST be first AND stored (uncompressed) per EPUB spec.
    $entries.Add(@{
        Name = 'mimetype'
        Data = [System.Text.Encoding]::ASCII.GetBytes('application/epub+zip')
        Stored = $true
    })

    $entries.Add(@{
        Name = 'META-INF/container.xml'
        Data = [System.Text.Encoding]::UTF8.GetBytes(@'
<?xml version="1.0" encoding="UTF-8"?>
<container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container">
  <rootfiles>
    <rootfile full-path="OEBPS/content.opf" media-type="application/oebps-package+xml"/>
  </rootfiles>
</container>
'@)
        Stored = $false
    })

    $manifestItems = New-Object System.Collections.Generic.List[string]
    $spineItems    = New-Object System.Collections.Generic.List[string]
    $navList       = New-Object System.Collections.Generic.List[string]

    for ($i = 0; $i -lt $Chapters.Count; $i++) {
        $n    = $i + 1
        $id   = "chap$n"
        $file = "chapter$n.xhtml"
        $body = $Chapters[$i] -replace '&', '&amp;' -replace '<', '&lt;'

        $xhtml = @"
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE html>
<html xmlns="http://www.w3.org/1999/xhtml" lang="en">
<head><meta charset="UTF-8"/><title>Chapter $n</title></head>
<body>
  <h1>Chapter $n</h1>
  <p>$body</p>
  <p>This is page content for chapter $n. The body repeats so the EPUB has enough material to scroll through and produce meaningful CFI locations when you stop and resume.</p>
  <p>$body</p>
  <p>$body</p>
</body>
</html>
"@
        $entries.Add(@{ Name = "OEBPS/$file"; Data = [System.Text.Encoding]::UTF8.GetBytes($xhtml); Stored = $false })
        $manifestItems.Add("    <item id=`"$id`" href=`"$file`" media-type=`"application/xhtml+xml`"/>")
        $spineItems.Add("    <itemref idref=`"$id`"/>")
        $navList.Add("      <li><a href=`"$file`">Chapter $n</a></li>")
    }

    $opf = @"
<?xml version="1.0" encoding="UTF-8"?>
<package xmlns="http://www.idpf.org/2007/opf" version="3.0" unique-identifier="bookid">
  <metadata xmlns:dc="http://purl.org/dc/elements/1.1/">
    <dc:identifier id="bookid">urn:uuid:$bookId</dc:identifier>
    <dc:title>$Title</dc:title>
    <dc:creator>$Author</dc:creator>
    <dc:language>en</dc:language>
    <meta property="dcterms:modified">$([DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ'))</meta>
  </metadata>
  <manifest>
    <item id="nav" href="nav.xhtml" media-type="application/xhtml+xml" properties="nav"/>
$($manifestItems -join "`n")
  </manifest>
  <spine>
$($spineItems -join "`n")
  </spine>
</package>
"@

    $nav = @"
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE html>
<html xmlns="http://www.w3.org/1999/xhtml" xmlns:epub="http://www.idpf.org/2007/ops" lang="en">
<head><meta charset="UTF-8"/><title>Contents</title></head>
<body>
  <nav epub:type="toc" id="toc">
    <h1>Contents</h1>
    <ol>
$($navList -join "`n")
    </ol>
  </nav>
</body>
</html>
"@
    $entries.Add(@{ Name = 'OEBPS/content.opf'; Data = [System.Text.Encoding]::UTF8.GetBytes($opf); Stored = $false })
    $entries.Add(@{ Name = 'OEBPS/nav.xhtml';   Data = [System.Text.Encoding]::UTF8.GetBytes($nav); Stored = $false })

    Write-Zip -Path $Path -Entries $entries.ToArray()
}

# ---------------------------------------------------------------- PDF ----------
# Hand-rolled minimal PDF 1.4 with correct xref offsets. One Helvetica font,
# letter-size pages, "Page N of M" centered.
function New-Pdf {
    param([string]$Path, [string]$Title, [int]$Pages)

    $sb = New-Object System.Text.StringBuilder
    $offsets = @()

    function Add-PdfObject {
        param($Builder, [int]$Num, [string]$Body)
        $script:offsets += $Builder.Length
        [void]$Builder.Append("$Num 0 obj`n$Body`nendobj`n")
    }

    # Header without the optional binary marker (avoids PowerShell escape issues
    # and is still a valid PDF — readers don't require it).
    [void]$sb.Append("%PDF-1.4`n")

    # Reserve object numbers:
    #   1: Catalog
    #   2: Pages (parent)
    #   3: Font
    #   4..(3+Pages):     Page objects
    #   (4+Pages)..(3+2P) Content streams
    $pageObjNums    = 4..(3 + $Pages)
    $contentObjNums = (4 + $Pages)..(3 + 2 * $Pages)

    Add-PdfObject $sb 1 "<< /Type /Catalog /Pages 2 0 R >>"

    $kids = ($pageObjNums | ForEach-Object { "$_ 0 R" }) -join ' '
    Add-PdfObject $sb 2 "<< /Type /Pages /Kids [$kids] /Count $Pages >>"

    Add-PdfObject $sb 3 "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"

    for ($i = 0; $i -lt $Pages; $i++) {
        $pageObj    = $pageObjNums[$i]
        $contentObj = $contentObjNums[$i]
        $pageBody = "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
                    "/Resources << /Font << /F1 3 0 R >> >> /Contents $contentObj 0 R >>"
        Add-PdfObject $sb $pageObj $pageBody
    }

    for ($i = 0; $i -lt $Pages; $i++) {
        $contentObj = $contentObjNums[$i]
        $n = $i + 1
        $stream = "BT /F1 36 Tf 1 0 0 1 100 700 Tm ($Title) Tj ET`n" +
                  "BT /F1 72 Tf 1 0 0 1 200 400 Tm (Page $n of $Pages) Tj ET"
        $body = "<< /Length $($stream.Length) >>`nstream`n$stream`nendstream"
        Add-PdfObject $sb $contentObj $body
    }

    $xrefStart = $sb.Length
    $totalObjs = 3 + 2 * $Pages
    [void]$sb.Append("xref`n0 $($totalObjs + 1)`n")
    [void]$sb.Append("0000000000 65535 f `n")
    foreach ($off in $offsets) {
        [void]$sb.Append(("{0:D10} 00000 n `n" -f $off))
    }
    [void]$sb.Append("trailer`n<< /Size $($totalObjs + 1) /Root 1 0 R >>`nstartxref`n$xrefStart`n%%EOF`n")

    [System.IO.File]::WriteAllText($Path, $sb.ToString(), [System.Text.Encoding]::ASCII)
}

# ---------------------------------------------------------------- Build --------
# Filenames follow SoftMedia's book parser convention: "Author - Title.ext".
# Titles are real public-domain works so the OpenLibrary metadata provider will
# return actual covers, descriptions, ISBNs, and publication dates — fully
# exercising the scan → parse → metadata → image-cache → detail-view pipeline.

# Wipe previous fixtures so renamed files don't leave stale copies behind.
if (Test-Path $out) {
    Get-ChildItem -Recurse -File $out | Remove-Item -Force -ErrorAction SilentlyContinue
}

Write-Host "Generating comic archives (CBZ)..." -ForegroundColor Cyan
$cbzDir = Join-Path $out 'cbz'
New-Item -ItemType Directory -Force $cbzDir | Out-Null

# Golden Age public-domain comics. Filename uses no " - " separator because
# SoftMedia's FileNameParser.ParseBook splits on " - " into (author, title);
# comics don't have an author field on the cover, so we keep the entire display
# name as a single title.
New-Cbz -Path (Join-Path $cbzDir 'Amazing-Man Comics Issue 005.cbz') `
        -Title 'The Beginning' -Pages 12 `
        -Series 'Amazing-Man Comics' -IssueNumber 5 -Year 1939 `
        -Publisher 'Centaur Publications' -Writer 'Bill Everett' `
        -Genre 'Superhero, Action' `
        -Summary "Bill Everett's mystic adventurer debuts, battling the Great Question in his first solo outing." `
        -Palette @(
            [System.Drawing.Color]::FromArgb(31, 60, 114),
            [System.Drawing.Color]::FromArgb(120, 40, 140),
            [System.Drawing.Color]::FromArgb(30, 110, 80),
            [System.Drawing.Color]::FromArgb(180, 80, 30))

# Unpadded filenames (page1..page25) — exercises backend NaturalStringComparer.
New-Cbz -Path (Join-Path $cbzDir 'Mystery Men Comics Issue 012.cbz') `
        -Title 'The Blue Beetle Returns' -Pages 25 `
        -UnpaddedNames $true `
        -Series 'Mystery Men Comics' -IssueNumber 12 -Year 1940 `
        -Publisher 'Fox Feature Syndicate' -Writer 'Will Eisner' `
        -Genre 'Superhero, Mystery' `
        -Summary 'Featuring The Blue Beetle, Rex Dexter of Mars, and Lt. Drake of Scotland Yard.' `
        -Palette @(
            [System.Drawing.Color]::FromArgb(20, 20, 60),
            [System.Drawing.Color]::FromArgb(180, 50, 90))

# Single-page edge case.
New-Cbz -Path (Join-Path $cbzDir 'Weird Fantasy Issue 013.cbz') `
        -Title 'The Last Page' -Pages 1 `
        -Series 'Weird Fantasy' -IssueNumber 13 -Year 1952 `
        -Publisher 'EC Comics' -Writer 'Al Feldstein' `
        -Genre 'Science Fiction, Horror' `
        -Summary 'A single weird tale from the golden age of EC Comics.' `
        -Palette @([System.Drawing.Color]::FromArgb(60, 90, 30))

Write-Host "Generating EPUB books..." -ForegroundColor Cyan
$epubDir = Join-Path $out 'epub'
New-Item -ItemType Directory -Force $epubDir | Out-Null

# Pride and Prejudice — multi-chapter, tests CFI resume across chapters.
New-Epub -Path (Join-Path $epubDir 'Jane Austen - Pride and Prejudice.epub') `
         -Title 'Pride and Prejudice' -Author 'Jane Austen' `
         -Chapters @(
            'It is a truth universally acknowledged, that a single man in possession of a good fortune, must be in want of a wife.',
            'Mr. Bennet was among the earliest of those who waited on Mr. Bingley. He had always intended to visit him.',
            'Not all that Mrs. Bennet, however, with the assistance of her five daughters, could ask on the subject.',
            'Of Mr. Darcys letter, Elizabeth was in no humour for conversation with any one but himself.',
            'With a book he was regardless of time; and on the present occasion he had, in addition to all this, the recollection.')

# Poe short story — single-chapter test for the scroll/CFI mechanics on short material.
New-Epub -Path (Join-Path $epubDir 'Edgar Allan Poe - The Tell-Tale Heart.epub') `
         -Title 'The Tell-Tale Heart' -Author 'Edgar Allan Poe' `
         -Chapters @('True! nervous, very, very dreadfully nervous I had been and am; but why will you say that I am mad?')

Write-Host "Generating PDF books..." -ForegroundColor Cyan
$pdfDir = Join-Path $out 'pdf'
New-Item -ItemType Directory -Force $pdfDir | Out-Null

New-Pdf -Path (Join-Path $pdfDir "Lewis Carroll - Alice's Adventures in Wonderland.pdf") `
        -Title "Alice's Adventures in Wonderland" -Pages 5
New-Pdf -Path (Join-Path $pdfDir 'Mary Shelley - Frankenstein.pdf') `
        -Title 'Frankenstein' -Pages 30

Write-Host ""
Write-Host "Done. Files written to: $out" -ForegroundColor Green
Get-ChildItem -Recurse $out | ForEach-Object {
    $size = if ($_.PSIsContainer) { '[DIR]' } else { '{0,8} bytes' -f $_.Length }
    Write-Host ("  {0}  {1}" -f $size, $_.FullName.Substring($out.Length + 1))
}
