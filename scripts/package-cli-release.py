"""Set the build version and public defaults for a main-channel CLI wheel."""
import json
import os
from pathlib import Path
from urllib.parse import urlsplit

package = Path("apps/cli/src/ringfour_cli")
defaults = {
    "api_url": os.environ["RINGFOUR_API_URL"],
    "auth0_domain": os.environ["AUTH0_DOMAIN"],
    "auth0_client_id": os.environ["AUTH0_CLI_CLIENT_ID"],
    "auth0_audience": os.environ["AUTH0_AUDIENCE"],
}
api = urlsplit(defaults["api_url"])
domain = urlsplit("https://" + defaults["auth0_domain"])
if (
    not all(defaults.values())
    or not os.environ["AUTH0_WEB_CLIENT_ID"]
    or api.scheme != "https"
    or not api.hostname
    or api.username or api.password or api.query or api.fragment
    or domain.netloc != domain.hostname
    or domain.path or domain.query or domain.fragment
):
    raise SystemExit("Configure the public Auth0 and HTTPS API Actions variables.")

version_file = package / "__init__.py"
version = version_file.read_text().split('"')[1]
(package / "release_defaults.json").write_text(json.dumps(defaults) + "\n")
