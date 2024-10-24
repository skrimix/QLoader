# generate NetSparkle appcast items

import sys
import requests
from datetime import datetime

release_tag = input("Enter release tag: ")
version_string = release_tag.split("-")[0]
version_code = input(f"Enter version code [{version_string[1:]}.0]: ") or f"{version_string[1:]}.0"


# ask if update is critical
while True:
    critical = input("Is this a critical update? (y/N): ")
    if critical.lower() == "y":
        critical = True
        break
    elif critical.lower() == "n" or critical == "":
        critical = False
        break
    else:
        print("Invalid input")
critical = "true" if critical else "false"

release_url = f'https://webdav.5698452.xyz/qloader_files/builds_tag/{release_tag}/'

# get modtime of release folder
response = requests.request("PROPFIND", release_url)
response.raise_for_status()
response_xml = response.text
    
# parse date
modtime = datetime.strptime(response_xml.split("<D:getlastmodified>")[1].split("</D:getlastmodified>")[0], "%a, %d %b %Y %H:%M:%S %Z")

# format date in "ddd, dd MMM yyyy HH:mm:ss zzz" format
published_date_string = modtime.strftime("%a, %d %b %Y %H:%M:%S +0000")


# generate linux-x64 appcast item
# get file size for linux-x64.tar.gz
response = requests.head(release_url + "linux-x64.tar.gz")
response.raise_for_status()
linux_x64_file_length = response.headers["Content-Length"]
linux_x64_appcast_item = f"""<item>
    <title>Version {version_string[1:]} Linux</title>
    <sparkle:releaseNotesLink>
    https://qloader.5698452.xyz/files/release_notes/{version_string}.md
    </sparkle:releaseNotesLink>
    <pubDate>{published_date_string}</pubDate>
    <enclosure url="https://webdav.5698452.xyz/qloader_files/builds_tag/{release_tag}/linux-x64.tar.gz"
                sparkle:version="{version_code}"
                sparkle:os="linux"
                sparkle:criticalUpdate="{critical}"
                length="{linux_x64_file_length}"
                type="application/octet-stream" />
</item>"""

# generate linux-arm64 appcast item
# get file size for linux-arm64.tar.gz
response = requests.get(release_url + "linux-arm64.tar.gz")
response.raise_for_status()
linux_arm64_file_length = response.headers["Content-Length"]
linux_arm64_appcast_item = f"""<item>
    <title>Version {version_string[1:]} Linux arm64</title>
    <sparkle:releaseNotesLink>
    https://qloader.5698452.xyz/files/release_notes/{version_string}.md
    </sparkle:releaseNotesLink>
    <pubDate>{published_date_string}</pubDate>
    <enclosure url="https://webdav.5698452.xyz/qloader_files/builds_tag/{release_tag}/linux-arm64.tar.gz"
                sparkle:version="{version_code}"
                sparkle:os="linux"
                sparkle:criticalUpdate="{critical}"
                length="{linux_x64_file_length}"
                type="application/octet-stream" />
</item>"""

# generate osx-x64 appcast item
# get file size for osx-x64.zip
response = requests.get(release_url + "osx-x64.zip")
response.raise_for_status()
osx_x64_file_length = response.headers["Content-Length"]
osx_x64_appcast_item = f"""<item>
    <title>Version {version_string[1:]} Mac</title>
    <sparkle:releaseNotesLink>
    https://qloader.5698452.xyz/files/release_notes/{version_string}.md
    </sparkle:releaseNotesLink>
    <pubDate>{published_date_string}</pubDate>
    <enclosure url="https://webdav.5698452.xyz/qloader_files/builds_tag/{release_tag}/osx-x64.zip"
                sparkle:version="{version_code}"
                sparkle:os="osx"
                sparkle:criticalUpdate="{critical}"
                length="{osx_x64_file_length}"
                type="application/octet-stream" />
</item>"""

# print appcast items
print("appcast.xml items:")
print(linux_x64_appcast_item)
print(osx_x64_appcast_item)

print("\nappcast_arm64.xml items:")
print(linux_arm64_appcast_item)