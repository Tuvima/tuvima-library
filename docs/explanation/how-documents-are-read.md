---
title: "How documents are read"
description: "See how Tuvima Library reads PDFs, Office files, scans and photos of paper so you can search them by their words, entirely on your own computer."
audience: user
category: explanation
product_area: view
status: current
tags:
  - "documents"
  - "ocr"
  - "view"
---

# How documents are read

Documents in your View library are searchable by their own words. Tuvima Library reads each file on your own computer, in the background, so folder scans stay quick and nothing is uploaded.

## Files that already contain text

PDFs with a text layer, Word, Excel and PowerPoint files, OpenDocument text and RTF are read directly. Plain text files are read during the folder scan.

## Scans and photos of paper

A scanned PDF has pictures of pages instead of text. A photo of a receipt is only a picture. Tuvima Library reads these with text recognition (OCR) using Tesseract, a free program that runs on your computer.

- **Which pages:** for each PDF page, if the file's own text layer holds fewer than 25 letters and digits, the page is drawn as an image and recognised. Pages that already have text are never re-read.
- **Which photos:** common picture formats (JPEG, PNG, TIFF, WebP, BMP, and HEIC when your computer can open it). Very small pictures are skipped.
- **Crooked or sideways pages:** if the first read looks poor, the page is checked for rotation and tried once more.
- **Mixed files:** a PDF with some typed pages and some scanned ones is marked as mixed, and both kinds of words are searchable.

Your original files are never changed, and no searchable copy of a PDF is written. The recognised words are kept in the search index, private to the person who owns the space.

## Quiet in the background

Recognition reads one file at a time. It waits while anyone is watching or listening to media, and picks up again afterwards. Each file has a time limit (five minutes by default) and PDFs are read up to 500 pages; a file that hits a limit keeps what was read and is marked partly read. If a re-read fails, the earlier words are kept. **Settings > Libraries > View > Documents** shows how many files are waiting and which one is being read.

## Settings

Under **Settings > Libraries > View > Documents** you can switch reading on or off, choose **Low** effort to make it gentler on a small server, and tick the languages to read with. English is included. Other languages are an optional download, and that download is the only time text recognition uses the internet. **Read all documents again** queues every PDF and photo that has no words yet.

If Tesseract is not installed, the section says **Text recognition is not installed** and everything else keeps working. Windows installers and containers include it. For other installs, see [configuration](../reference/configuration.md#configocrjson).

For the whole Documents page see [Documents in View](documents-in-view.md). For the privacy picture see [privacy and network access](privacy-local-first.md).
