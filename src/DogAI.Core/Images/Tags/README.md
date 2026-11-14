# The bundled tag list

`tag-list.txt` is compiled into DOG.ai and read by `TagList` (Core/Images). DOG.ai checks every tag the chat model
writes for a picture against it; nothing is looked up online.

## Where it comes from

- **Source file:** `tags/danbooru_e621_merged.csv` from the GitHub project
  [DominikDoom/a1111-sd-webui-tagcomplete](https://github.com/DominikDoom/a1111-sd-webui-tagcomplete),
  commit `40ad070a02` (2025-01-04), with tags as of 2024-12-22 and a 25-post cutoff.
  Downloaded 2026-11-14 with the owner's approval; 6,024,803 bytes,
  SHA-256 `AA9EAB8435562F86D7AC0D81F2F7BEDCBA9F6B93A806010708702406BBA85E18`.
- **In the app:** Models › On this PC › Pictures › Tags can download this same file (from that commit, checked against this
  SHA-256) or take a copy chosen on the PC, kept as `<models>\image\tags\full-tag-list.csv` and read with the same rules.
- **Licence:** that project is MIT-licensed; its licence text is `LICENSE-tagcomplete.txt` in this folder. The tag
  names, other spellings and post counts in the file are the public tag data of Danbooru and e621.
- **Owner's decision (2026-11-14):** the merged Danbooru + e621 file rather than Danbooru alone, because the owner's
  checkpoints include furry ones that know e621 tags such as `anthro`.

## What is in it

29,452 tags: Danbooru's general tags, e621's general tags and e621's species tags, each with at least 100 posts,
with their post count and other spellings.

Left out on purpose:

- **tags the owner chose to leave out (2026-11-14):** they are listed only as SHA-256 fingerprints in
  `excluded-tags.txt`, never as words. The tag check drops a written tag that is, or contains, one of them, instead
  of correcting it into a tag nearby. The owner can add the full list in the app (Models › On this PC › Pictures › Tags),
  which brings them back;
- **character and series tags:** one of these makes a checkpoint draw an existing character, over the card's own Looks;
- **artist tags:** one of these changes the whole style to imitate that artist, and many look like ordinary words, so
  a spelling correction could land on one;
- **meta and lore tags:** they describe the file on the site, not the picture;
- **tags with fewer than 100 posts:** a checkpoint has barely learned them.

None of this limits the owner: a card's Looks and a checkpoint's quality words go into the prompt as typed.

## Rebuilding it

Download the source file by hand, then:

```
pwsh tools/make-taglist.ps1 -Source <path to danbooru_e621_merged.csv>
```

The script builds DogAI.Core and uses its own rules (`TagCsv`), so the bundled list and a full list added in the app
are read the same way. It leaves out every tag and other spelling whose fingerprint is in `excluded-tags.txt`, and
never prints one. Update the numbers above afterwards, and run `dotnet test` (tests check that everyday tags and every
framing the model may choose are in the list, and that no listed fingerprint is). With `DOGAI_TEST_TAG_CSV` set to the
source file, a test also checks that the rules rebuild this list exactly and that every left-out tag and spelling is
dropped by the tag check.

## Leaving out more tags

Write the tags in a plain text file **outside the repository**, one a line (in any spelling the sites use), then:

```
pwsh tools/tag-fingerprints.ps1 -Words <that file>
pwsh tools/make-taglist.ps1 -Source <path to danbooru_e621_merged.csv>
```

The first adds their fingerprints (SHA-256 of the tag in lower case with spaces for underscores) to
`excluded-tags.txt` and prints only counts. To put a tag back, correct the private word list, cut `excluded-tags.txt`
down to its `#` header lines, and run both commands again with the whole list. Never write the tags themselves into a file in the repository, a commit
message, a test or the log. Tests use made-up tags.
