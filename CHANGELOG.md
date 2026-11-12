# Changelog

## Unreleased

- Reading a bulk job returns only the items for nodes the user can browse. The job list hides jobs the user did not start unless they can browse one of its nodes.
- Alt text checks the image size against the 4.5 MB limit before reading the file into memory.
- A test asserts every API controller carries an `[Authorize(Policy = ...)]` attribute.
- README: the key ring is not protected at rest, and a timed-out call is not counted against the monthly cap.
