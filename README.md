YouCast
=======

# Important Notice !!!

**This is a fork of the original YouCast project by I3arnon**

This fork is work in progress. Fixes are not tested and may not work as expected.

**Note:** This fork requires `ffmpeg` to be installed or `ffmpeg` binaries to be present in the same folder.

Added functionality:
- local muxing of audio and video streams
- caching of muxed video files
- cache setting tab used for deleting cached files


# Original README

[![All Releases](https://img.shields.io/github/downloads/i3arnon/YouCast/total.svg)](https://github.com/i3arnon/YouCast/releases)
[![Release](https://img.shields.io/github/release/i3arnon/YouCast.svg)](https://github.com/i3arnon/YouCast/releases)
[![License](https://img.shields.io/github/license/i3arnon/YouCast.svg)](LICENSE)

YouCast allows you to subscribe to channels and playlists on YouTube as video and audio podcasts in any standard podcast app (e.g. Overcast, iTunes, BeyondPod, etc.).

<p align="center"><img style="display: block; margin-left: auto; margin-right: auto;" src="https://raw.githubusercontent.com/I3arnon/YouCast/master/src/Screenshot.PNG" alt="Screenshot" width="463" height="295" /></p>

<p align="center"><a href="http://www.paypal.com/cgi-bin/webscr?cmd=_s-xclick&amp;hosted_button_id=B8VLNS5S6UBEE"><img style="display: block; margin-left: auto; margin-right: auto;" src="http://www.paypalobjects.com/en_US/i/btn/btn_donateCC_LG_global.gif" alt="" /></a></p>

## Features
 - Subscribe to any channel's public video uploads.
 - Subscribe to any public playlist on YouTube.
 - Get audio-only feeds (for faster downloads).
 - Sort the videos by popularity (awesome for huge channels, like TED Talks)
 - Run YouCast at home and access it from everywhere on the internet, not just your home network.
 - Limit the amount of videos in any feed (helps with users with thousands of videos).
 - Use YouCast from any device in your network: iPhone, Android, Tablet. (Just open the incoming port 22703 on your firewall and paste the URL in your podcast app)

## Usage
To get a URL for a podcast:

1. Open YouCast
2. Enter a YouTube channel's username or playlist ID.
3. Choose audio or video quality (the better the quality the bigger the file).
4. Generate and copy the podcast URL.
5. Paste the URL in your favorite podcast app.
6. While your app is updating podcasts YouCast must be running.

### Setting your own API Key
Google restricts the amount of requests you can make per day with each API key. In order to circumvent this limit, you can set your own API key.

1. Go to the [Google Developers Console](https://console.developers.google.com/).
2. Make a new application. It can take a couple of minutes for the application to be generated by Google.
3. Assign the Youtube Data API v3 to the application.
4. Generate an API key for Youtube Data API v3.
5. Add the Application name and API key in the YouCast program, then click on save.

## Known Issues

1. Explicit/restricted videos can't be downloaded (they require YouCast to login with a user)
2. Audio feed episodes on iOS are 2X in length (second half is silent)
3. Muxed streams have stopped working (because of YouTube changes - [YoutubeExplode info](https://github.com/Tyrrrz/YoutubeExplode/releases/tag/6.4.2))
