tiles-cyber-data = data path
tiles-cyber-node = node pad
tiles-cyber-bus = data bus
tiles-cyber-static = static

cyberspace-beyond-deck = {CAPITALIZE(THE($device))} is beyond your deck: it needs to see it, within 8 tiles.
cyberspace-no-signal = {CAPITALIZE(THE($device))} has no signal: it isn't on a working network.
cyberspace-device-dead = {CAPITALIZE(THE($device))} is dead.
cyberspace-already-jacked-in = You're already jacked in.
cyberspace-dumpshocked = Your head is still ringing from the dumpshock. Give it {$seconds} more {$seconds ->
    [one] second
   *[other] seconds
}.
cyberspace-no-mind = There's nobody in there to jack in.
cyberspace-practice-full = The practice grid is full right now. Try again soon.
cyberspace-practice-failed = The deck can't raise a practice grid.

cyberspace-jack-in = You jack the deck into {THE($device)}. The world falls away into light.
cyberspace-jack-in-others = {CAPITALIZE(THE($user))} jacks a cyberdeck into {THE($device)} and goes still.
cyberspace-jack-in-remote = Your deck reaches into {THE($device)} through the air, and its network opens. The world falls away into light.
cyberspace-jack-in-remote-others = {CAPITALIZE(THE($user))} points a cyberdeck at {THE($device)} and goes still.
cyberspace-jack-in-practice = You jack into your deck's practice grid. The room falls away into light.
cyberspace-jack-in-practice-others = {CAPITALIZE(THE($user))} jacks into a cyberdeck and goes still.
cyberspace-examined-jacked-in = [color=lightblue]{ CAPITALIZE(SUBJECT($ent)) } { CONJUGATE-HAVE($ent) } a far-off, unblinking stare. { CAPITALIZE(POSS-ADJ($ent)) } mind is somewhere in cyberspace.[/color]

cyberspace-jack-out = You jack out. The world comes back.
cyberspace-lost-connection = You lose the connection.
cyberspace-deck-torn = Your deck is torn from your hands! You slam back into your body.
cyberspace-lost-device = The machine you're jacked into drops out from under you. Dumpshock!
cyberspace-lost-remote = You lose the machine you were hacking, and the network with it. Dumpshock!
cyberspace-path-dissolves = The path dissolves under you, and you with it. Dumpshock!
cyberspace-body-down = Your body hits the floor and the connection goes with it. Dumpshock!
cyberspace-avatar-died = Your avatar flatlines, and you slam back into a body that won't answer. Dumpshock!

cyberspace-node-no-ui = There's nothing to open at {THE($node)}.
cyberspace-breaching = {CAPITALIZE(THE($node))} is locked. You start breaching it...
cyberspace-breached = You breach {THE($node)}.
cyberspace-firewall-open = {CAPITALIZE(THE($node))} lets you through.

cyberspace-ward-up = Your ward goes up.
cyberspace-strike = You strike {THE($target)}. Integrity {$integrity}%.
cyberspace-struck = {CAPITALIZE(THE($striker))} strikes you! Integrity {$integrity}%.
cyberspace-strike-cut-out = You cut {THE($target)} out of cyberspace.
cyberspace-cut-down = Another runner cuts you down. Dumpshock!
cyberspace-strike-ice = You strike the ICE. Its integrity {$integrity}%.
cyberspace-ice-derezzed = The ICE shatters and derezzes!

cyberspace-ice-examined = ICE, guarding this network. Integrity {$integrity}%.
cyberspace-ice-strikes = ICE strikes you! Integrity {$integrity}%.
cyberspace-ice-strikes-warded = ICE strikes you! Integrity {$integrity}%. Your ward takes half.
cyberspace-ice-dumpshock = ICE tears through what's left of you. Dumpshock! You come to with your head burning.
cyberspace-ice-practice-out = The ICE tears through you, and the practice grid throws you out.

cyberspace-program-run = You run {$file}.
cyberspace-program-run-at = You run {$file} at {THE($target)}.
cyberspace-program-run-others = {CAPITALIZE(THE($user))} runs {$file}.
cyberspace-program-run-at-others = {CAPITALIZE(THE($user))} runs {$file} at {THE($target)}.
cyberspace-program-wont-run = {$file} won't run: {$why}.
cyberspace-program-dropped = You let go of {$file}; it derezzes. It's still on your deck.

species-name-cyber-avatar = Avatar
reagent-name-ghostlight = ghostlight
reagent-desc-ghostlight = What a virtual body bleeds: a runner's signal, leaking out of them as glowing light.

cyberspace-proxy-id-reflects = It shows {THE($id)}, worn by its runner's body.
cyberspace-proxy-id-blank = It's blank: its runner's body wears no ID.
cyberspace-proxy-id-name = proxy ID{$jobSuffix}
cyberspace-proxy-id-full-name = {$fullName}'s proxy ID{$jobSuffix}
