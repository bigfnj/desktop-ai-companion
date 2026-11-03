# The Zoom calendar (Reminder 1.3.0)

Calendar 5 in **Options → Reminders** is the **Zoom** card. It reads the **scheduled meetings you host** in Zoom,
straight from Zoom, and, if you tick **Also read meetings I'm invited to**, the meetings you are invited to in the next
24 hours. The companion announces them like any other calendar's events, with Join and the join question.

What it reads, and what it does not:

- **Meetings you host:** scheduled meetings, and each occurrence of a recurring meeting with fixed times. The window
  is the next 48 hours, like the other calendars.
- **Not instant meetings, and not recurring meetings with no fixed time:** there is no time to remind you about.
- **Meetings other people host and invited you to: only if you tick "Also read meetings I'm invited to".** Zoom's
  main meetings list holds only a host's own meetings. Usually those invites also reach your Outlook or `.ics`
  calendar, which another card already reads. When they reach Zoom but no calendar this app can read (your Zoom
  account has a calendar connected, so the Zoom app shows them, but that calendar cannot be added here), tick the
  switch. The card then also asks Zoom for the meetings you are invited to in the **next 24 hours**, the window Zoom
  gives that request. Invited meetings are announced with Join like your own. Their accept or decline answer is not
  in what Zoom sends, so **Skip meetings I've declined** cannot skip them on Zoom's word alone. When the same invite
  is also on a calendar card here, that copy carries your answer and decides: an invite you declined in Outlook
  stays silent.
- **Not yet confirmed (1.3.0):** Zoom documents that request as "meetings the user is invited to", but not whether that
  covers every invite a connected calendar brings in, or only invites made in Zoom itself (an alternative host, a
  registration). Ticking the switch is how you find out for your account: if a meeting you see in the Zoom app never
  appears on the card, Zoom does not hand that one to other apps.
- **A known Zoom fault (2026):** for some accounts Zoom answers this request with an empty reply instead of a list.
  It has been reported on accounts with a connected calendar. Zoom acknowledged it on its developer forum and
  tentatively targeted a fix for its November 2026 release; the Zoom Meetings API changelog says when it actually
  ships. The card then says **Zoom sent an empty answer for the meetings you are invited to**. That tells you
  nothing yet about your invites; check again once the changelog lists the fix.
- **A meeting on both** (your Zoom meeting that is also on your Outlook calendar) is announced **once**, from the
  calendar, in that calendar's name and style. The Zoom card counts it as "also on a calendar".
- **The same Zoom meeting on two calendars** (an invite that reached both Outlook and Gmail, say) is also announced
  **once** while the Zoom card is on, from the first calendar in slot order. With the Zoom card Off, each calendar
  announces its own copy, as before 1.3.0.
- Both rules work when the calendar copy's join link carries the meeting number (`/j/<number>`). A personal-room link
  (`/my/<name>`) does not, and such a meeting is announced from each copy.

## One-time setup: make your own Zoom app

The companion has no Zoom app of its own, so you make one in Zoom's App Marketplace and paste its client ID. Only
you use it. It needs no secret, and it is never published.

1. Sign in at [marketplace.zoom.us](https://marketplace.zoom.us) with the Zoom account whose meetings you want, then
   **Develop → Build App**, and choose a **General App**.
2. Make it **user-managed** (each user signs in for themselves), not admin-managed.
3. Under **OAuth**, turn on **Use Public Client OAuth** (PKCE). Zoom then shows a **public client ID**. Copy that
   one: it is the one the companion needs, not the confidential client ID with its secret.
4. Add this redirect URL, and add the same address to the OAuth allow list:

   ```
   http://127.0.0.1/zoom/callback
   ```

   No port: the companion listens on a free port each time it signs in, and Zoom ignores the port of a `127.0.0.1`
   address when it matches. `localhost` does not work; Zoom requires the numeric address.
5. Under **Scopes**, add **`meeting:read:list_meetings`** and **`meeting:read:meeting`**. For **Also read meetings
   I'm invited to**, add **`meeting:read:list_upcoming_meetings`** as well. All three are read-only: the app can list
   your meetings and read a meeting's details, and cannot create, change or delete anything. A scope added after you
   signed in takes effect when you press **Sign in to Zoom…** again.
6. Leave the app in development. You can sign in to an app you are developing with your own account.

If your Zoom account belongs to an organisation, its Zoom admin may require apps to be approved before anyone can add
them. If the sign-in page says the app is not allowed, that is the admin's setting: ask them to approve the app, or
use the account it is allowed on.

## Signing in

1. On the **Zoom** card, set **Feed type** to **Zoom**, paste the public client ID, and press **Apply**.
2. Press **Sign in to Zoom…**. Your browser opens Zoom's own sign-in and consent page. The companion never sees your
   Zoom password.
3. Allow the app. The browser lands on a short page saying Zoom has answered, and you can close that tab. The card's
   **Zoom sign-in** line says when the sign-in is done, and your meetings appear within a few seconds (**Check now**
   reads them at once).

The sign-in waits three minutes for the browser. **Sign out of Zoom**, an **Apply** that changes the client ID or
turns the card Off, or closing the app abandons a sign-in that is still waiting, and that sign-in is not kept.

## What is kept, and where

- **The sign-in:** Zoom's refresh token and the client ID, encrypted with Windows DPAPI for your Windows user, in the
  Reminder module's own data folder as `zoom-signin.dpapi`. It is not in `settings.json`, which is plain text.
- **The access token:** it lasts an hour and is held in memory only. The companion renews it when it is about to
  run out. Zoom replaces the refresh token on every renewal; the new one is kept in memory and saved at once. If that
  save fails, the card warns, the sign-in keeps working until the app closes, and the save is retried on every read.
- **Never written anywhere:** no meeting title, join link, code or token goes to the diagnostic log. The log records
  "zoom: signed in" and "zoom: signed out" and, as for every calendar, a short reason when a read fails.

**Sign out of Zoom** deletes the saved sign-in. It does not tell Zoom: the app keeps its permission at Zoom until you
remove it under **Manage → Added Apps** on the Zoom App Marketplace.

## When something is wrong

The **Zoom sign-in** line and the **Feed status** card say what happened:

| It says | What it means |
|---|---|
| Not signed in | Press **Sign in to Zoom…**. |
| Zoom no longer accepts this sign-in | The refresh token was revoked or ran out (Zoom keeps one for 90 days of no use). Sign in again. |
| The Zoom app is missing a scope it needs | Add the scopes it names (step 5), then sign in again. |
| The meetings you are invited to could not be read | The invited read failed; the meetings you host are still read. If it names `meeting:read:list_upcoming_meetings`, add that scope and sign in again, or untick the switch. |
| Zoom sent an empty answer for the meetings you are invited to | Zoom's own fault, described above: it answered with nothing at all, not even an empty list. The meetings you host are still read. Check again once Zoom's changelog lists the fix, or untick the switch to clear the ⚠. |
| …could not be expanded / could not be read / were left out | Zoom's answer was incomplete. The meetings that did arrive are still announced, and the card shows ⚠ until a full read succeeds. |
| The renewed Zoom sign-in could not be saved | The module's folder could not be written. It keeps working until the app closes and retries the save on every read. |
| Signed in with a different client ID | You changed the client ID after signing in. Sign in again. |

**Not yet confirmed with a live Zoom account (1.3.0).** Zoom's documentation shows how a public client trades the
sign-in code for tokens, which is what this does. For the hourly renewal it shows only the confidential-client form,
so the renewal uses the public form by inference. If Zoom refuses it, the card says so about an hour after you sign
in. That hour is the live check in SMOKETEST.
