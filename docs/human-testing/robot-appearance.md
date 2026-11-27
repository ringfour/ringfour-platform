# Check robot images and icons

After the operator rebuilds/reloads the Platform API and Console, sign in as
an owner. The API applies the new migration at startup. The virtual robot
stack does not need changes.

1. Open Robots. `virt2` should show the supplied Base101 image. Open its detail
   page and confirm the same image in the header. If there are multiple robots
   named `virt2`, choose the intended one and set its image manually below.
2. In Appearance choose Rover, save, refresh the page and return to Robots.
   Check both the list and Home robot rows show Rover. Choose Base101 image
   and save to restore the requested image.
3. Upload a PNG, JPEG or WebP photo. Check its preview, save, refresh and check
   the list/header/Home use the same photo. Try an unsupported file, then a
   file larger than 10 MiB; both should leave the old choice intact and explain
   the error. A failed save must keep the chosen image available to retry.
4. Open Connect a robot, enter a real device's pairing code, select an image or
   icon and connect. Check the newly enrolled robot retains that choice after
   a refresh. Repeat with an existing unregistered target if available.
5. As a developer/operator, confirm appearance remains visible but editing
   controls are absent. Direct write requests must receive 403; a user outside
   the robot's workspace must receive 404. Null via the documented PUT endpoint
   resets to the default icon; missing appearance and remote/SVG images fail.
6. After a normal operator-managed API reload, confirm selections persist.
   Disconnect/reconnect a device and check it does not overwrite the image.

These live checks require an operator-managed reload and real pairing code;
component fixtures do not constitute live acceptance.
