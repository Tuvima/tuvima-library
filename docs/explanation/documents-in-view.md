---
title: "Documents in View"
description: "A plain overview of the Documents page in View: what Tuvima Library reads, where it keeps it, how tags and filing work, and what stays on your computer."
audience: user
category: explanation
product_area: view
status: current
tags:
  - "documents"
  - "view"
  - "privacy"
---

# Documents in View

**View > Documents** is where the PDFs, Word files, spreadsheets, scans and photos of paper in your View folders are found, read, searched and filed. It sits beside Photos, Galleries and Folders in the View menu. Your files stay where they are. Tuvima Library builds a searchable, organised list on top of them.

## What is read

Tuvima Library reads the words inside your documents so you can search by what a document says, not only by its file name.

- PDFs that already contain text, Word, Excel and PowerPoint files, OpenDocument text and RTF are read directly.
- Plain text files (`.txt`, `.md`, `.csv`, `.json` and `.xml`) are read when the folder is scanned.
- A scanned PDF or a photo of a receipt or letter is read with text recognition (OCR). Only the pages that have no text of their own are recognised.

Reading happens in the background, one file at a time, so a big folder scan stays quick. Scanned pages wait while anyone is watching or listening to something. Older Office formats (`.doc`, `.xls`, `.ppt`) and OpenDocument spreadsheets and slides (`.ods`, `.odp`) appear in the list and can be filed and downloaded, but their words are not read. [How documents are read](how-documents-are-read.md) has the details and the limits.

## Where it lives and what stays on your computer

Everything happens on your own computer. The words are kept in the search index of your data store, inside the Personal Space of the person who owns the document. They are never shown on Home, in the global search or in recommendations, and they are never uploaded.

- **Your originals are never changed.** No searchable copy of a PDF is written either.
- **Text recognition runs locally.** It uses Tesseract, a free program on your host. English comes with Tuvima Library. If you tick another language, Settings downloads that language file from GitHub when you press Download. That optional download is the only time text recognition uses the internet.
- **The PDF viewer is bundled.** It loads from Tuvima Library itself, not from a content network.
- **Suggestions use no AI.** They are worked out from the document's own words and from documents you filed earlier, in the same library only.

For the wider picture see [privacy and network access](privacy-local-first.md).

## Tags, correspondents, types and filing

You organise a document with a few simple labels.

- **Tags** are short labels such as "Taxes". They work on photos and videos too.
- **A correspondent** is who the document came from or is about, such as a bank or a clinic.
- **A document type** is what kind of document it is, such as an invoice or a letter.
- **A document date**, an archive number and notes.
- **Custom fields** you define yourself, such as "Policy number" or "Amount due".

Tags, correspondents, types and custom fields are one list for the whole server, but you only see the names that are used on documents you can open, plus any you made yourself. Deleting a name never deletes a document.

A new document first waits in your **Inbox**. Each Inbox card shows what Tuvima Library thinks the date, correspondent, type and tags are, and you accept them with one tap, change them first, or file the document as it is. **Auto-filing rules** can do the same for you when a document looks a certain way, for example "when the words contain the name of my bank, add the tag Banking". Nothing is filed silently except by a rule you made. [How documents are filed](how-documents-are-filed.md) explains suggestions and rules.

## Tags a rule adds can come back

Tuvima Library does not keep a record of tags you take off. If an auto-filing rule added a tag and you remove it by hand, the tag can come back when the rule applies again, for example after "Apply rules to existing documents" or when the document's words are read again. Editing the rule so it no longer matches, changing the tag it adds, or deleting the rule stops it. A value you set or cleared yourself is never replaced by a rule.

## Finding and keeping your lists

- **Search and filters.** Search the words, or narrow the list by tag, correspondent, type, date and custom fields. The list can be sorted by date added, document date, title or correspondent, and your choice is remembered for your profile.
- **Saved views.** Save a set of filters under a name, such as "Tax this month", and open it again later. A relative date range such as "Last 30 days" is worked out afresh each time. Saved views are private to you. They cannot be shared, and they never appear as a source for a Collection.
- **Select and bulk edit.** Choose several documents to add or remove tags, set a correspondent, type or date, mark them filed, or move them to the trash, up to 500 at a time. Moving to the trash can be undone, and no file is deleted.
- **Export.** Download the list you are looking at as a spreadsheet (CSV). It holds titles, dates, correspondents, types, tags and custom fields, never the words inside the documents or your notes.

You can search, filter and file from the Inbox on a phone. Editing every detail of a document and editing rules need a computer or tablet. A television can browse and open documents but not change them.

## The Shared Library

You can send documents to the household's Shared Library. A shared copy keeps its tags, date, correspondent, type, archive number, notes and custom field values. Documents that sit in the Shared Library can show up in its Inbox. A household administrator or the server administrator files them from the Inbox card with **File**, or in bulk with **Mark filed**, so the Shared Inbox can be emptied; everyone else, including child profiles, sees those cards read-only with the note "Filed by a household administrator". Other bulk edits skip Shared documents, and household administrators' rules can still file them automatically.

## Who can see Documents

Documents is a permission, set under **Settings > Libraries > View > Who can see Documents**. It is on for everyone except child profiles, which start with it off until an administrator turns it on. A profile without it does not see the Documents tab, and its saved views, document words and files are not offered to it anywhere.

## Related

- [How documents are read](how-documents-are-read.md)
- [How documents are filed](how-documents-are-filed.md)
- [Use your Personal Space in View](../guides/view-personal-space.md)
- [Privacy and network access](privacy-local-first.md)
