# Extended 16:9 view

Open **F2 ? Display ? Aspect ratio** and select **Extended view (16:9)**.
A 1920?1080 window fills the screen with more level visible on either side.
Original pixels, Television (4:3), and Stretch remain available.

The extended renderer produces 342?224 pixels, up from 256?224. This retains
approximately the same television pixel proportions at 16:9. It does not stretch
Mario or crop the top and bottom. The status bar stays centered at its original size.

## Supported scenes

Stock SMW horizontal levels with ordinary layer 1 terrain are extended. The view
clamps at level boundaries. The added terrain comes from the complete live Map16 map; it does
not expose stale columns from the original scrolling VRAM ring. The central
viewport retains the original VRAM sampling and dynamic-block timing. Tile animation,
palettes, priorities, color math, windows, and sprite interpolation still apply.

Sprite spawning, despawning, and the three shared sprite drawing routines use
wider horizontal bounds. This is an opt-in gameplay visibility change: enemies can
become active earlier. The simulation rate, physics constants, audio clock, and
presentation cap are unchanged. A newly enabled view may need camera movement to
populate sprites beyond the old spawn boundary; enter a level with 16:9 enabled
for a consistent initial population.

Overworld maps, single-screen rooms, vertical levels, interactive layer 2 levels,
and special boss/scripted scenes retain their original centered framing with side
borders. They are not advertised as extended scenes. These need separate camera,
map layout, and sprite-specific work. ROM hacks with a different layout are not
supported by the SMW adaptations; the builder validates cartridge data signatures
before enabling them. Unrecognized SMW layouts keep the original centered view.

Rebuild the game with the updated builder. Older generated executables cannot gain
this renderer from a settings change. New SMW builds profile both normal and wide
play paths. The strict player still contains no runtime CPU translator or interpreter.

## Local verification

`dotnet test Smw2Pc.sln -c Release` checks center-pixel parity, both sides of OAM's
nine-bit coordinates, and existing original-view state/video/audio baselines.
`./tools/test-homebrew-gameplay.ps1` and `./tools/test-player-ui.ps1` exercise the
compiled player and switching viewports while paused. Their original homebrew
fixtures and captures remain development-only artifacts.

A local ROM-specific player can run `--headless --profile wide-play --frames 3600
--screenshot wide.bmp`. Input replays can set `"Widescreen": true`; viewport choice
is included in the script hash. `--verify` also checks the generated wide profiles.
Commercial ROMs, saves, and screenshots are not committed or uploaded by CI.
