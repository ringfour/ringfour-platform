# Robot appearance

This is new Console metadata design. It does not change Agent profiles,
device enrollment credentials, connectivity, or robot capabilities.

Robot responses (detail, lists and pairing results) include nullable
`appearance`. Create-robot and pairing-claim requests accept it optionally.
An omitted pairing choice preserves the existing robot's appearance.

Owners can `PUT /v1/robots/{robot_id}/appearance` with
`{"appearance":"base101"}`. The field is required; null resets to the default
robot icon. Success returns the updated Robot. Non-members receive 404;
members without the owner role receive 403. Invalid values receive 400
`invalid_input`. Each successful replacement records an actor-attributed
`appearance_updated` robot audit event.

Allowed values: `bot`, `rover`, `arm`, `drone`, `base101`, or a canonical
base64 PNG/JPEG/WebP data URL. The API checks the MIME/signature match and a
256 KiB decoded limit. Remote URLs and SVG are rejected. The Console accepts
image files up to 10 MiB and resizes them to at most 512px per side before
encoding. The original supplied `base101.png` is a bundled Console asset;
the database stores its key rather than a duplicate image. The bundled copy
is cropped with PIL from `(420, 145, 1620, 1015)` and resized to 720 × 522;
the original source remains untouched.

EF migration `20261126110845_RobotAppearance` adds nullable `robots.appearance`
and extends the audit type constraint. It selects `base101` for the existing
robot named `virt2`, as requested, only when exactly one robot has that name.
Ambiguous names are left unchanged and can be set by an owner in the Console.
Other existing robots retain the default icon. The migration is transactional
and is applied by normal API startup. Downgrade removes the column and the
new audit type's events before restoring the previous constraint.

Home and Robots use the same prominent robot card: a 220 × 176 image before
a large robot title, connectivity, profile, current/latest App and last seen.
Actions remain attached to the exact robot/installation. Narrow screens stack
the image above the title. The detail header places a 220 × 168 image directly
before the title.
Enrollment includes the image/icon picker. Owners can subsequently change it
in the robot detail page's Appearance card.
