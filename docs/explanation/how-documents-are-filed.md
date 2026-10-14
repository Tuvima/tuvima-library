---
title: "How documents are filed"
description: "See how new documents land in an Inbox, how suggestions and auto-filing rules help you file them, and what Tuvima Library never does on its own."
audience: user
category: explanation
product_area: view
status: current
tags:
  - "documents"
  - "inbox"
  - "rules"
  - "view"
---

# How documents are filed

Every new document that Tuvima Library finds in your View sources lands in your **Inbox** on the Documents page. Filing a document means giving it a date, a correspondent (who sent or issued it), a type and tags. Filing never changes the original file.

## The Inbox

The Inbox is the first choice in the Documents filter rail, with a count such as "12 to file". The Documents tab in the View menu shows the same count as a small badge, and only while it is above zero.

Each Inbox card shows the thumbnail, the title, the date found in the document, and chips for the suggested tags, correspondent and type.

- **File** accepts everything on the card in one tap (or press Enter on a computer). It files exactly what the card shows, so a suggestion that arrives while the card is open is never filed unseen.
- **Tap a chip** to change it first: pick another name, clear it, or switch a suggested tag off.
- **File without changes** only marks the document as filed.

You can file from a phone. Editing everything about a document, and editing rules, happens on a computer or tablet.

## Suggestions

Suggestions are worked out on your own computer, with no AI and nothing sent anywhere:

- **Date.** Dates written as 2026-03-04, 4 March 2026, March 4, 2026 or 4.3.2026 are found in the document's words. Dates next to "invoice date", "issued" or "dated" win over dates next to "due", "expires" or "born". A date like 03/04/2026 that could be read two ways is not used. If no date is found, the file's own date is shown instead, and it is never written as the document date for you.
- **Correspondent, type and tags.** The document's rarest words are compared with documents you filed earlier. When at least two of the five most alike documents share a correspondent, type or tag, it is suggested.
- **Amount.** Only when you have a custom field named Amount, and only when the document clearly says "amount due", "total due" or "grand total" next to a single number.

Suggestions are shown, never applied silently. They only draw on documents in the same library, so they can never reveal a name from someone else's private documents.

## Auto-filing rules

A rule says "when a document looks like this, do that". Make them from the **Auto-filing rules** button on the Documents page (on a computer or tablet; a phone can see them but not change them). Administrators find the same list in Settings, Libraries, View.

- **Conditions:** the words contain a word or phrase, the file name contains text, the folder path contains text, the document came from a chosen View source, or the file is a PDF, image or Office document. Matching ignores letter case. There are no patterns or wildcards. Choose whether **all** or **any** of the conditions must hold.
- **Actions:** add tags, set the correspondent, set the document type, set a custom field's value, and optionally **file automatically**.
- **Order:** rules run top to bottom. Every matching rule applies. Tags from all of them are added; for the correspondent, the type and each field, the first rule that sets it wins.
- **Inbox or filed:** a rule that files automatically files the document on arrival. Any other rule leaves the document in the Inbox with its values already filled in.
- **Try it:** "Test on my documents" lists up to ten of your documents the rule would match.
- **Apply to existing documents** runs your rules again over documents you already have.

A rule never replaces something you set yourself, including a value you cleared. A value a rule set earlier can be replaced by a rule. Tags are only ever added, so a tag a rule adds comes back the next time the rule runs if you take it off by hand, because Tuvima Library does not remember that you removed it. Change the rule instead.

### Whose rules, whose documents

A rule belongs to the person who made it and only applies to documents in their own Personal Space. A household administrator can also make rules for the household's Shared Library. Rules run in the background after a document's words have been read, so a scanned document is filed as soon as its text recognition finishes.

## Shared Library copies

When a document is copied to the Shared Library, its date, correspondent, type, tags, archive number, notes and custom field values go with it, as a filed document.

Documents that were added to the Shared Library from a shared folder can show up in the Shared Library's Inbox. A household administrator or the server administrator can file them by hand from the Inbox card, or in bulk with Mark filed, to empty the Shared Inbox. Everyone else, including child profiles, sees those cards read-only with the note "Filed by a household administrator", and the auto-filing rules of a household administrator still file them automatically.

Related: [Documents in View](documents-in-view.md) and [How documents are read](how-documents-are-read.md).
