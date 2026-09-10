# Proposed fortune categories: 12 down to 7

Applied to packs/collections.json. Generated from an explicit mapping that is checked to be a
complete partition of all 159 sources, so nothing can silently fall into the "More packs"
fallback the way it did before.

The `origin` column below is **as of the proposal**, before the corpus was cut to two sources. Only
`fortunes` and `dadjokes` are built-in now; everything else reads `pack`. `docs/FORTUNE-SOURCE-TABLE.md`
is the regenerated, current version.

## Why these seven

**Tone is not a subject.** Three of the old categories described how something sounds rather
than what it is about: "Spicy", and the clean/mature split across Pop-Culture TV. The app
already has an ordered Content level control plus a profanity switch, and both act on the
per-fortune `level` field, not on the collection. The `vibe` field in `collections.json` is
read by no code at all. So those splits duplicated a filter that already exists and worked
better than they did.

**A category of one is a heading, not a category.** Dad Jokes, BOFH Excuses and Reddit
Showerthoughts each occupied a top-level row for a single source. That is three rows of the
tree spent on navigation that leads nowhere.

**NSFW stays separate, and it is the one exception to the first rule.** It is a safety
boundary rather than a subject, and it should never be something you switch on by accident
while browsing for jokes.

## The proposal

| category | sources | fortunes |
|---|---:|---:|
| Jokes & Humour | 16 | 7,721 |
| Fortunes & Wisdom | 30 | 8,539 |
| Books, Art & Language | 19 | 7,924 |
| Screen & Stage | 54 | 14,404 |
| Tech & Hackers | 11 | 3,664 |
| Facts & Observations | 10 | 14,363 |
| NSFW (adults only) | 19 | 4,555 |

### Jokes & Humour

Punchlines, puns, and the comedians who deal in one-liners.

16 sources, ~7,721 fortunes.

| source id | display name | fortunes | origin | was |
|---|---|---:|---|---|
| `dadjokes` | Dad Jokes | 2,794 | pack | Dad Jokes **←** |
| `cleanjokes` | Clean Jokes | 1,588 | built-in | Comedy & One-liners **←** |
| `yo-mama` | Yo Mama Jokes | 978 | pack | Spicy **←** |
| `chuckfacts` | Chuck Norris Facts | 436 | pack | Facts & Trivia **←** |
| `handey` | Jack Handey: Deep Thoughts | 368 | pack | Comedy & One-liners **←** |
| `entertainers` | Entertainers | 353 | pack | Comedy & One-liners **←** |
| `redgreen` | The Red Green Show | 253 | pack | Comedy & One-liners **←** |
| `humorists` | Humorists | 188 | pack | Comedy & One-liners **←** |
| `mencken` | H. L. Mencken | 151 | pack | Philosophy & Wisdom **←** |
| `critics` | Critics | 121 | pack | Literary & Creative **←** |
| `pirate` | Pirate Speak | 118 | pack | Comedy & One-liners **←** |
| `subgenius` | Church of the SubGenius | 105 | pack | Spicy **←** |
| `groucho` | Groucho Marx | 78 | pack | Comedy & One-liners **←** |
| `SeventyMaximsOfMaximallyEffectiveMercenaries` | 70 Maxims of Maximally Effective Mercenaries | 69 | pack | Comedy & One-liners **←** |
| `paradoxum` | Paradoxes & Oxymorons | 68 | pack | Philosophy & Wisdom **←** |
| `carlin` | George Carlin | 53 | pack | Spicy **←** |

### Fortunes & Wisdom

The fortune-cookie register, plus philosophy and aphorism. What the feature is named after.

30 sources, ~8,539 fortunes.

| source id | display name | fortunes | origin | was |
|---|---|---:|---|---|
| `quotable` | Quotable | 2,109 | built-in | Philosophy & Wisdom **←** |
| `classic_philosophy` | Classical Philosophy | 1,071 | pack | Philosophy & Wisdom **←** |
| `modern_philosophy` | Modern Philosophy | 974 | pack | Philosophy & Wisdom **←** |
| `platitudes` | Platitudes | 487 | pack | Philosophy & Wisdom **←** |
| `fortunes` | Classic Fortunes | 431 | built-in | Comedy & One-liners **←** |
| `godin` | Seth Godin | 401 | built-in | Philosophy & Wisdom **←** |
| `tao` | Tao Te Ching | 391 | pack | Philosophy & Wisdom **←** |
| `wisdom` | Wisdom Quotes | 381 | pack | Philosophy & Wisdom **←** |
| `Rousseau` | Jean-Jacques Rousseau | 373 | pack | Philosophy & Wisdom **←** |
| `activists` | Activists & Civil Rights | 357 | built-in | Philosophy & Wisdom **←** |
| `Paine` | Thomas Paine | 317 | pack | Philosophy & Wisdom **←** |
| `education` | Education Quotes | 181 | pack | Philosophy & Wisdom **←** |
| `MrRogers` | Mister Rogers | 141 | pack | Comedy & One-liners **←** |
| `Gurdjieff` | G. I. Gurdjieff | 120 | pack | Philosophy & Wisdom **←** |
| `HeraclitusFragments` | Heraclitus: Fragments | 109 | pack | Philosophy & Wisdom **←** |
| `montaigne` | Michel de Montaigne | 106 | pack | Philosophy & Wisdom **←** |
| `immortal_consciousness` | The Key to Immortal Consciousness | 82 | pack | Philosophy & Wisdom **←** |
| `SimoneWeil` | Simone Weil | 79 | pack | Philosophy & Wisdom **←** |
| `actualcookies` | Fortune Cookies | 71 | pack | Facts & Trivia **←** |
| `jung` | Carl Jung | 71 | pack | Philosophy & Wisdom **←** |
| `RAW` | Robert Anton Wilson | 69 | pack | Spicy **←** |
| `invisiblestates` | The Invisible States | 50 | pack | Philosophy & Wisdom **←** |
| `goedel` | Kurt Gödel | 43 | pack | Philosophy & Wisdom **←** |
| `Bakunin` | Mikhail Bakunin | 38 | pack | Philosophy & Wisdom **←** |
| `stevenson` | Adlai Stevenson | 29 | both | Literary & Creative **←** |
| `Twenty_Lessons_On_Tyranny` | Twenty Lessons on Tyranny | 19 | pack | Philosophy & Wisdom **←** |
| `haraway` | Donna Haraway | 16 | pack | Philosophy & Wisdom **←** |
| `bruno-latour` | Bruno Latour | 11 | pack | Philosophy & Wisdom **←** |
| `korzybski` | Alfred Korzybski | 7 | pack | Philosophy & Wisdom **←** |
| `Schlesinger` | Arthur Schlesinger | 5 | pack | Philosophy & Wisdom **←** |

### Books, Art & Language

Authors, artists, poets, and play with the language itself.

19 sources, ~7,924 fortunes.

| source id | display name | fortunes | origin | was |
|---|---|---:|---|---|
| `authors` | Authors | 3,167 | both | Literary & Creative **←** |
| `artists` | Artists | 1,625 | both | Literary & Creative **←** |
| `pratchett` | Terry Pratchett | 548 | pack | Literary & Creative **←** |
| `songs-poems` | Songs & Poems | 433 | pack | Literary & Creative **←** |
| `art` | Art Quotes | 395 | pack | Literary & Creative **←** |
| `rhetorical-devices` | Rhetorical Devices | 318 | both | Literary & Creative **←** |
| `EnglishAsSheIsSpoke` | English As She Is Spoke | 304 | both | Literary & Creative **←** |
| `ObliqueStrategies` | Oblique Strategies (Eno & Schmidt) | 272 | both | Literary & Creative **←** |
| `literature` | Literature Quotes | 212 | pack | Literary & Creative **←** |
| `anathem-glossary` | Anathem Glossary (Neal Stephenson) | 160 | pack | Literary & Creative **←** |
| `wblake` | William Blake | 156 | both | Literary & Creative **←** |
| `Jenny_Holzer` | Jenny Holzer: Truisms | 119 | both | Literary & Creative **←** |
| `ogden_nash` | Ogden Nash | 98 | both | Literary & Creative **←** |
| `BibleAbridged` | The Bible, Abridged | 48 | built-in | Literary & Creative **←** |
| `Kerouac-Modern-Prose` | Kerouac on Modern Prose | 30 | pack | Literary & Creative **←** |
| `ObscureSorrows` | Dictionary of Obscure Sorrows | 16 | both | Literary & Creative **←** |
| `brecht_dances-events-puzzles` | Brecht: Dances, Events & Puzzles | 12 | pack | Philosophy & Wisdom **←** |
| `friedman_12-structures` | Ken Friedman: Event Scores | 7 | pack | Philosophy & Wisdom **←** |
| `racter` | Racter (early AI prose) | 4 | pack | Literary & Creative **←** |

### Screen & Stage

Television and film. One category now: the per-fortune content level already separates clean from mature, so splitting it here duplicated a filter that exists.

54 sources, ~14,404 fortunes.

| source id | display name | fortunes | origin | was |
|---|---|---:|---|---|
| `tv-simpsons` | The Simpsons | 1,573 | pack | Pop-Culture TV (clean) **←** |
| `tv-mst3k` | Mystery Science Theater 3000 | 1,554 | pack | Pop-Culture TV (clean) **←** |
| `tv-southpark` | South Park | 862 | pack | Pop-Culture TV (mature) **←** |
| `tv-futurama` | Futurama | 754 | pack | Pop-Culture TV (clean) **←** |
| `tv-beavisbutthead` | Beavis and Butt-Head | 751 | pack | Pop-Culture TV (mature) **←** |
| `tv-peepshow` | Peep Show | 544 | pack | Pop-Culture TV (mature) **←** |
| `tv-venturebros` | The Venture Bros. | 382 | pack | Pop-Culture TV (mature) **←** |
| `tv-drawntogether` | Drawn Together | 381 | pack | Pop-Culture TV (mature) **←** |
| `tv-malcolm` | Malcolm in the Middle | 379 | pack | Pop-Culture TV (clean) **←** |
| `SimpsonsChalkboard` | Simpsons Chalkboard Gags | 365 | built-in | Pop-Culture TV (clean) **←** |
| `tv-koth` | King of the Hill | 363 | pack | Pop-Culture TV (clean) **←** |
| `tv-archer` | Archer | 342 | pack | Pop-Culture TV (mature) **←** |
| `tv-x-files` | The X-Files | 331 | pack | Pop-Culture TV (clean) **←** |
| `tv-tpb` | Trailer Park Boys | 326 | pack | Pop-Culture TV (mature) **←** |
| `FerengiRulesOfAcquisition` | Ferengi Rules of Acquisition | 294 | pack | Facts & Trivia **←** |
| `tv-sealab2021` | Sealab 2021 | 255 | pack | Pop-Culture TV (mature) **←** |
| `tv-office-us` | The Office (US) | 236 | pack | Pop-Culture TV (clean) **←** |
| `tv-sopranos` | The Sopranos | 234 | pack | Pop-Culture TV (mature) **←** |
| `startrek` | Star Trek | 220 | pack | Pop-Culture TV (clean) **←** |
| `tv-alwayssunny` | It's Always Sunny in Philadelphia | 220 | pack | Pop-Culture TV (mature) **←** |
| `tv-30rock` | 30 Rock | 214 | pack | Pop-Culture TV (clean) **←** |
| `tv-qi` | QI | 210 | pack | Pop-Culture TV (clean) **←** |
| `tv-mrshow` | Mr. Show | 208 | pack | Pop-Culture TV (mature) **←** |
| `tv-madmen` | Mad Men | 192 | pack | Pop-Culture TV (clean) **←** |
| `tv-firefly` | Firefly | 189 | pack | Pop-Culture TV (clean) **←** |
| `tv-seinfeld` | Seinfeld | 182 | pack | Pop-Culture TV (clean) **←** |
| `tv-scrubs` | Scrubs | 180 | pack | Pop-Culture TV (clean) **←** |
| `tv-thewire` | The Wire | 179 | pack | Pop-Culture TV (mature) **←** |
| `tv-3rdrock` | 3rd Rock from the Sun | 167 | pack | Pop-Culture TV (mature) **←** |
| `tv-metalocalypse` | Metalocalypse | 164 | pack | Pop-Culture TV (mature) **←** |
| `tv-batman` | Batman (1966) | 163 | pack | Pop-Culture TV (clean) **←** |
| `tv-moralorel` | Moral Orel | 153 | pack | Pop-Culture TV (clean) **←** |
| `tv-friskydingo` | Frisky Dingo | 152 | pack | Pop-Culture TV (mature) **←** |
| `tv-boondocks` | The Boondocks | 150 | pack | Pop-Culture TV (mature) **←** |
| `tv-arrested` | Arrested Development | 149 | pack | Pop-Culture TV (clean) **←** |
| `tv-parksrec` | Parks and Recreation | 149 | pack | Pop-Culture TV (clean) **←** |
| `tv-homemovies` | Home Movies | 135 | pack | Pop-Culture TV (clean) **←** |
| `tv-curb` | Curb Your Enthusiasm | 119 | pack | Pop-Culture TV (mature) **←** |
| `tv-harveybirdman` | Harvey Birdman, Attorney at Law | 110 | pack | Pop-Culture TV (clean) **←** |
| `tv-rockos` | Rocko's Modern Life | 92 | pack | Pop-Culture TV (clean) **←** |
| `tv-snl` | Saturday Night Live | 91 | pack | Pop-Culture TV (clean) **←** |
| `tv-squidbillies` | Squidbillies | 88 | pack | Pop-Culture TV (mature) **←** |
| `tv-batman-tas` | Batman: The Animated Series | 80 | pack | Pop-Culture TV (clean) **←** |
| `tv-newsradio` | NewsRadio | 79 | pack | Pop-Culture TV (clean) **←** |
| `tv-youngones` | The Young Ones | 62 | pack | Pop-Culture TV (mature) **←** |
| `tv-twilightzone` | The Twilight Zone | 54 | pack | Pop-Culture TV (clean) **←** |
| `tv-montypython` | Monty Python's Flying Circus | 46 | pack | Pop-Culture TV (clean) **←** |
| `tv-lookaroundyou` | Look Around You | 45 | pack | Pop-Culture TV (clean) **←** |
| `tv-bobsburgers` | Bob's Burgers | 44 | pack | Pop-Culture TV (clean) **←** |
| `tv-dilbert` | Dilbert | 43 | pack | Pop-Culture TV (clean) **←** |
| `tv-genkill` | Generation Kill | 43 | pack | Pop-Culture TV (mature) **←** |
| `tv-lucydevil` | Lucy, the Daughter of the Devil | 41 | pack | Pop-Culture TV (mature) **←** |
| `tv-robotchicken` | Robot Chicken | 38 | pack | Pop-Culture TV (mature) **←** |
| `tv-a-team` | The A-Team | 27 | pack | Pop-Culture TV (clean) **←** |

### Tech & Hackers

Computing, programming, and the culture around it.

11 sources, ~3,664 fortunes.

| source id | display name | fortunes | origin | was |
|---|---|---:|---|---|
| `computers` | Computing Quotes | 812 | pack | Tech & Hackers |
| `hackers` | Hacker Culture | 750 | both | Tech & Hackers |
| `lwall-quotes` | Larry Wall (Perl's creator) | 739 | both | Tech & Hackers |
| `bofh` | BOFH Excuses | 489 | pack | BOFH Excuses **←** |
| `linux` | Linux & Open Source | 346 | pack | Tech & Hackers |
| `epigrams_in_programming` | Epigrams in Programming | 240 | both | Tech & Hackers |
| `ComputerDictionary` | Computer Dictionary | 114 | both | Tech & Hackers |
| `enkiv2s-glossary-of-tech-industry-terms` | Tech Industry Glossary | 89 | both | Tech & Hackers |
| `perl` | Perl Wisdom | 37 | pack | Tech & Hackers |
| `hacker-questions` | Hacker Questions | 26 | both | Tech & Hackers |
| `rfc1925` | RFC 1925: The Twelve Networking Truths | 22 | both | Tech & Hackers |

### Facts & Observations

Things that are true, things that sound true, and noticing.

10 sources, ~14,363 fortunes.

| source id | display name | fortunes | origin | was |
|---|---|---:|---|---|
| `showerthoughts` | Showerthoughts (Reddit) | 10,533 | both | Reddit Showerthoughts **←** |
| `realfacts` | Random Facts | 1,722 | both | Facts & Trivia **←** |
| `people` | Notable People | 1,181 | pack | Facts & Trivia **←** |
| `science` | Science Quotes | 505 | pack | Facts & Trivia **←** |
| `food` | Food & Cooking | 164 | pack | Facts & Trivia **←** |
| `sports` | Sports Quotes | 101 | pack | Facts & Trivia **←** |
| `medicine` | Medicine | 50 | pack | Facts & Trivia **←** |
| `pets` | Pets & Animals | 47 | pack | Facts & Trivia **←** |
| `news` | News & Journalism | 44 | pack | Facts & Trivia **←** |
| `predictions` | Famously Wrong Predictions | 16 | pack | Philosophy & Wisdom **←** |

### NSFW (adults only)

The classic `fortune -o` set. Kept separate on purpose: this is the one grouping that is a safety boundary rather than a subject, so it should never be something you enable by accident while browsing.

19 sources, ~4,555 fortunes.

| source id | display name | fortunes | origin | was |
|---|---|---:|---|---|
| `off-atheism` | Atheism & Religion Jabs | 1,357 | pack | NSFW (fortune -o) **←** |
| `off-limerick` | Bawdy Limericks | 964 | pack | NSFW (fortune -o) **←** |
| `off-sex` | Sex Jokes | 592 | pack | NSFW (fortune -o) **←** |
| `off-definitions` | Cynical Definitions | 304 | pack | NSFW (fortune -o) **←** |
| `off-politics` | Political Humor (crude) | 252 | pack | NSFW (fortune -o) **←** |
| `off-riddles` | Bawdy Riddles | 247 | pack | NSFW (fortune -o) **←** |
| `off-black-humor` | Black Humor | 172 | pack | NSFW (fortune -o) **←** |
| `off-religion` | Religious Humor (crude) | 153 | pack | NSFW (fortune -o) **←** |
| `off-vulgarity` | Vulgarity | 140 | pack | NSFW (fortune -o) **←** |
| `off-songs-poems` | Bawdy Songs & Poems | 114 | pack | NSFW (fortune -o) **←** |
| `off-privates` | Anatomy Jokes | 76 | pack | NSFW (fortune -o) **←** |
| `off-drugs` | Drug Humor | 56 | pack | NSFW (fortune -o) **←** |
| `off-miscellaneous` | Miscellaneous (crude) | 52 | pack | NSFW (fortune -o) **←** |
| `off-astrology` | Astrology (crude) | 32 | pack | NSFW (fortune -o) **←** |
| `off-debian` | Debian In-Jokes (crude) | 19 | pack | NSFW (fortune -o) **←** |
| `off-knghtbrd` | Knghtbrd IRC Quotes | 13 | pack | NSFW (fortune -o) **←** |
| `off-linux` | Linux In-Jokes (crude) | 7 | pack | NSFW (fortune -o) **←** |
| `off-zippy` | Zippy the Pinhead | 3 | pack | NSFW (fortune -o) **←** |
| `off-art` | Art (crude) | 2 | pack | NSFW (fortune -o) **←** |

---

## Moves worth explaining

- **Showerthoughts (Reddit)** (`showerthoughts`, 10,533): Its own single-source category before, at 10.5k the largest source in the library. It is observational, so it anchors Facts & Observations rather than standing alone.
- **Dad Jokes** (`dadjokes`, 2,794): Its own single-source category before. It is a joke pack, so it sits with the jokes.
- **Notable People** (`people`, 1,181): Notable People is quotes-by-person, which reads as trivia. Left in Facts for now, flagged below as a judgement call.
- **Yo Mama Jokes** (`yo-mama`, 978): Jokes. Was in "Spicy", which described tone rather than subject.
- **BOFH Excuses** (`bofh`, 489): Its own single-source category before. It is computing humour, so it sits in Tech.
- **Chuck Norris Facts** (`chuckfacts`, 436): Chuck Norris Facts are jokes with a fact-shaped delivery. They were under Facts & Trivia next to real science quotes.
- **Classic Fortunes** (`fortunes`, 431): The complaint that started this. It is the BSD fortune file: 245 quips, 62 uplifting, 42 dark, 36 wisdom, 22 aphorism. "You will be given a post of trust and responsibility." That is the fortune-cookie register, not comedy.
- **Ferengi Rules of Acquisition** (`FerengiRulesOfAcquisition`, 294): Star Trek, so it belongs with the screen material. It was under Facts & Trivia.
- **Star Trek** (`startrek`, 220): Same, and it was already adjacent but in a TV category that is now merged.
- **H. L. Mencken** (`mencken`, 151): A satirist. Sits with the wits rather than the philosophers.
- **Mister Rogers** (`MrRogers`, 141): Was under Comedy & One-liners. He is not a comedian.
- **Critics** (`critics`, 121): Critics are quotable for their barbs, so humour rather than literature.
- **Church of the SubGenius** (`subgenius`, 105): Church of the SubGenius is parody religion, so it reads as humour rather than wisdom.
- **Fortune Cookies** (`actualcookies`, 71): Joins Classic Fortunes for the same reason. These two belong together and were two categories apart.
- **Robert Anton Wilson** (`RAW`, 69): Robert Anton Wilson is counterculture philosophy, not a tone. Was in "Spicy".
- **Paradoxes & Oxymorons** (`paradoxum`, 68): Paradoxes and oxymorons are wordplay, not philosophy.
- **George Carlin** (`carlin`, 53): A comedian. Was in "Spicy" for the same reason.
- **Adlai Stevenson** (`stevenson`, 29): Adlai Stevenson is quoted for political wisdom, not literature.
- **Famously Wrong Predictions** (`predictions`, 16): Famously Wrong Predictions is trivia, not philosophy.
- **Brecht: Dances, Events & Puzzles** (`brecht_dances-events-puzzles`, 12): Event scores are creative prompts, so they sit with Oblique Strategies rather than with philosophy.
- **Ken Friedman: Event Scores** (`friedman_12-structures`, 7): Same, event scores.

## Judgement calls I would like you to check

1. **`people` (Notable People, 1,181)** sits in Facts & Observations because it reads as
   trivia, but it is really quotes-by-person and could equally anchor a "Quotes" category.
   Leaving it in Facts avoids an eighth category; say the word if you want the split.
2. **`off-debian` and `off-linux`** are tech in-jokes but stay in NSFW, because they are part
   of the classic `fortune -o` set and are crude. Moving them into Tech would put crude
   material in a category nobody expects it in.
3. **Fortunes & Wisdom is the biggest at 30 sources**, carrying a long tail of tiny
   philosophers (Schlesinger 5, Korzybski 7, Friedman 7, Latour 11). Splitting philosophy from
   the fortune-cookie register would make an eighth category and put Classic Fortunes back on
   its own. I think the long tail is fine: it is a browse tree, not a menu.
4. **Screen & Stage is 53 sources**, the largest by count. It is one flat list of shows. If
   that is unwieldy in the tree, the honest fix is sub-grouping in the UI rather than
   splitting the category back apart by tone.

## Cost of applying it

`packs/collections.json` is embedded in the Fortunes DLL, so this needs a module rebuild and
republish, then a catalog regeneration. That is the exact path that shipped the "More packs"
bug, where a `-SkipBuild` publish re-zipped stale bits. The freshness gate now verifies the
shipped DLL actually contains the current mappings, so a repeat would fail the gate.
