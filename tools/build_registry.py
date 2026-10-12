#!/usr/bin/env python3
"""Build a static, npm-compatible Unity package registry (served by GitHub Pages).

Unity's Package Manager only needs a few read-only endpoints from a scoped registry:
  GET /<package-name>        packument: every version's package.json + tarball URL + sha1
  GET /-/v1/search?text=…    search results ("My Registries" listing); Pages ignores the query string
  GET /-/all                 legacy listing, same data
so the whole registry is a handful of static files.

Published versions = every `v*` git tag, plus the package.json version at HEAD (so a release is just
"bump version, push"). A tag always wins over HEAD for the same version: published versions never change.
Tarballs are deterministic (fixed mtimes/order/gzip header), so rebuilding never changes a version's sha1.

  python3 tools/build_registry.py --out _site [--base-url https://owner.github.io/repo]
"""
import argparse, gzip, hashlib, io, json, os, re, subprocess, sys, tarfile, tempfile
from datetime import datetime, timezone

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))


def git(*args):
    return subprocess.run(["git", "-C", ROOT, *args], check=True, capture_output=True, text=True).stdout


def package_dir():
    dirs = [d for d in os.listdir(ROOT) if os.path.isfile(os.path.join(ROOT, d, "package.json"))]
    if len(dirs) != 1:
        sys.exit("expected exactly one package folder (with package.json) at the repo root, found: %s" % dirs)
    return dirs[0]


def semver_key(v):
    m = re.match(r"^(\d+)\.(\d+)\.(\d+)(?:-(.+))?$", v)
    if not m:
        return (0, 0, 0, 0, v)
    major, minor, patch, pre = m.groups()
    return (int(major), int(minor), int(patch), 0 if pre else 1, pre or "")


def files_at(ref, pkg):
    """{relative path: bytes} of the package folder at a git ref (None = working tree)."""
    out = {}
    if ref is None:
        base = os.path.join(ROOT, pkg)
        for d, _, names in os.walk(base):
            for n in names:
                full = os.path.join(d, n)
                out[os.path.relpath(full, base).replace(os.sep, "/")] = (open(full, "rb").read(), os.access(full, os.X_OK))
        return out
    listing = git("ls-tree", "-r", ref, "--", pkg + "/")
    for line in listing.splitlines():
        meta, path = line.split("\t", 1)
        mode, _, blob = meta.split()
        data = subprocess.run(["git", "-C", ROOT, "cat-file", "blob", blob], check=True, capture_output=True).stdout
        out[path[len(pkg) + 1:]] = (data, mode == "100755")
    return out


def tarball(files):
    """Deterministic UPM tarball: everything under package/, sorted, fixed metadata."""
    raw = io.BytesIO()
    with tarfile.open(fileobj=raw, mode="w", format=tarfile.USTAR_FORMAT) as tar:
        for path in sorted(files):
            data, executable = files[path]
            info = tarfile.TarInfo("package/" + path)
            info.size = len(data)
            info.mtime = 0
            info.mode = 0o755 if executable else 0o644
            info.uname = info.gname = ""
            tar.addfile(info, io.BytesIO(data))
    gz = io.BytesIO()
    with gzip.GzipFile(fileobj=gz, mode="wb", mtime=0) as f:
        f.write(raw.getvalue())
    return gz.getvalue()


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--out", default="_site")
    ap.add_argument("--base-url", default=None, help="public URL of the registry (default: from GITHUB_REPOSITORY)")
    args = ap.parse_args()

    base = args.base_url
    if not base:
        repo = os.environ.get("GITHUB_REPOSITORY")
        if not repo:
            sys.exit("pass --base-url (or run inside GitHub Actions)")
        owner, name = repo.split("/")
        base = "https://%s.github.io/%s" % (owner.lower(), name)
    base = base.rstrip("/")

    pkg = package_dir()
    sources = {}  # version -> (files, iso date)
    head_files = files_at(None, pkg)
    head_version = json.loads(head_files["package.json"][0])["version"]
    for tag in git("tag", "--list", "v*").split():
        files = files_at(tag, pkg)
        if "package.json" not in files:
            continue
        v = json.loads(files["package.json"][0])["version"]
        date = git("log", "-1", "--format=%cI", tag).strip()
        sources[v] = (files, date)
    if head_version not in sources:
        sources[head_version] = (head_files, datetime.now(timezone.utc).isoformat())

    os.makedirs(os.path.join(args.out, "tarballs"), exist_ok=True)
    os.makedirs(os.path.join(args.out, "-", "v1"), exist_ok=True)
    name = json.loads(head_files["package.json"][0])["name"]
    versions, times = {}, {}
    for v in sorted(sources, key=semver_key):
        files, date = sources[v]
        manifest = json.loads(files["package.json"][0])
        if manifest["name"] != name:
            continue  # renamed since; old name isn't served by this packument
        blob = tarball(files)
        fname = "%s-%s.tar.gz" % (name, v)  # not .tgz: Pages gzips octet-stream again, breaking UPM size checks
        with open(os.path.join(args.out, "tarballs", fname), "wb") as f:
            f.write(blob)
        manifest.update({"_id": "%s@%s" % (name, v),
                         "dist": {"tarball": "%s/tarballs/%s" % (base, fname), "shasum": hashlib.sha1(blob).hexdigest()}})
        versions[v] = manifest
        times[v] = date

    latest = max(versions, key=semver_key)
    head = versions[latest]
    times.update({"created": min(times.values()), "modified": max(times.values())})
    packument = {"_id": name, "name": name, "description": head.get("description", ""),
                 "dist-tags": {"latest": latest}, "versions": versions, "time": times,
                 "author": head.get("author"), "readme": ""}
    with open(os.path.join(args.out, name), "w") as f:
        json.dump(packument, f, indent=1)

    entry = {"name": name, "displayName": head.get("displayName"), "version": latest,
             "description": head.get("description", ""), "keywords": head.get("keywords", []),
             "date": times[latest], "author": head.get("author"), "links": {}}
    with open(os.path.join(args.out, "-", "v1", "search"), "w") as f:
        json.dump({"objects": [{"package": entry, "score": {"final": 1}, "searchScore": 1}], "total": 1,
                   "time": times["modified"]}, f)
    with open(os.path.join(args.out, "-", "all"), "w") as f:
        json.dump({"_updated": 0, name: dict(entry, **{"dist-tags": {"latest": latest},
                                                        "versions": {v: "latest" if v == latest else "" for v in versions}})}, f)

    scope = ".".join(name.split(".")[:2])
    with open(os.path.join(args.out, "index.html"), "w") as f:
        f.write("""<!doctype html><meta charset="utf-8"><title>%(name)s registry</title>
<body style="font:15px/1.5 system-ui;max-width:46em;margin:3em auto;padding:0 1em">
<h1>%(display)s</h1><p>A Unity scoped registry. Add it in <b>Project Settings ▸ Package Manager ▸ Scoped Registries</b>:</p>
<pre>Name:  %(display)s
URL:   %(base)s
Scope: %(scope)s</pre><p>Latest: <b>%(latest)s</b> · versions: %(versions)s</p>
<p><a href="https://github.com/%(repo)s">Source on GitHub</a></p>
""" % {"name": name, "display": head.get("displayName", name), "base": base, "scope": scope, "latest": latest,
       "versions": ", ".join(sorted(versions, key=semver_key, reverse=True)),
       "repo": os.environ.get("GITHUB_REPOSITORY", "")})
    open(os.path.join(args.out, ".nojekyll"), "w").close()

    print("registry for %s → %s (%s)" % (name, base, ", ".join(sorted(versions, key=semver_key))))
    print("latest=%s" % latest)
    if os.environ.get("GITHUB_OUTPUT"):
        with open(os.environ["GITHUB_OUTPUT"], "a") as f:
            f.write("latest=%s\nhead_version=%s\n" % (latest, head_version))


if __name__ == "__main__":
    main()
