# Files — Everything Search Edition

[![Build](https://github.com/DDwsgood/Files-Everything/actions/workflows/everything-build.yml/badge.svg)](https://github.com/DDwsgood/Files-Everything/actions/workflows/everything-build.yml)

This is a minimal fork of [files-community/Files](https://github.com/files-community/Files) that swaps the search
backend to [Everything](https://www.voidtools.com/) (voidtools) wherever Everything can answer. Everything else —
the UI, settings, tags, and behaviors — is untouched upstream Files.

**简述**：只把 Files 底层搜索换成 Everything 的最小化 fork。UI 与原版一致，只是搜索更快、支持 Everything 搜索语法；标签、AQS、WSL、网络位置自动回退到 Files 原生搜索，回收站内容不会出现在搜索结果里。

## How the search behaves

- **Plain searches** (e.g. `report`, `notes.txt`, `notepad*`) go to Everything and keep the same wildcard semantics
  as the native search — only much faster, across the whole indexed drive scope of the current folder.
- **Everything search syntax works** for the terms that resolve straight from the file index (verified instant):
  `ext:`, `dm:`, `size:`, `path:`, `parent:`, `file:`, `folder:`, `dupe:`, `regex:`, `wregex:`, `wfn:`,
  `wholefile:`, `wholepath:`, `nopath:`, `child:`, `count:`, `top:`, `offset:`, `depth:`, `nosubfolders:`,
  `run:`, `runcount:`, `recent:`, `case:`, `diacritics:`, `attrib`-free queries like `foo AND bar`, `!term`,
  `foo|bar`.
- **Everything property queries are routed to the native search on purpose**: Everything 1.5 resolves shell
  properties (e.g. `artist:`, `album:`, `length:`, `owner:`, `dimensions:`, `rating:`, and also `dc:`, `da:`,
  `attrib:`) through the shell property system, which was measured to stall the external IPC indefinitely on
  this beta. Queries whose colon terms are not in the verified list always use the built-in search.
- **Windows AQS keeps working natively**: queries that start with `$` (e.g. `$System.Kind:=document`) or use the
  Windows AQS vocabulary (`kind:`, `type:`, `datemodified:`, `author:`, `System.*`, …) fall back to the
  built-in Windows Search / Win32 search.
- **Tags** (`tag:…`) keep using Files' own tag database.
- **Fallback by location**: WSL (`\\wsl$`), network shares, removable/CD/ReFS/FAT volumes, and the Recycle Bin use
  the native search (Everything only indexes local NTFS volumes).
- **Recycle Bin filtering**: files under `X:\$RECYCLE.BIN` exist in Everything's index but are always excluded
  from results.
- **Auto-wake**: if Everything isn't running, Files tries to start it (discovered from the registry or the default
  install locations, launched with `-startup`) and polls until the database is loaded. If that fails, the native
  search is used, so Files always works — with or without Everything installed.
- No settings, no toggles, no UI changes. Install Everything (1.4 or the 1.5 beta both work) and search gets fast.

## Install

Grab the sideload packages from [Releases](../../releases) (self-signed MSIX bundle for x64 and ARM64), or the
store version of upstream Files will not include this integration. Everything 1.5 beta: https://www.voidtools.com/
(Install Everything 1.5 beta, and leave "Install Everything Service" enabled for admin-free indexing.)

## Bundled third-party components

- `src/Files.App/Everything32.dll`, `Everything64.dll`, `EverythingARM64.dll` — the official
  [Everything SDK](https://www.voidtools.com/support/everything/sdk/) IPC client DLLs, redistributed under
  [Everything-SDK-License.txt](src/Files.App/Everything-SDK-License.txt).
- Everything itself is **not** bundled; it must be installed separately.

## Credits & licenses

- [Files Community](https://files.community) — the base app (MIT / MPL-2.0, see `LICENSE-MIT`, `LICENSE-MPL`).
- Search-backend design informed by [NextFE](https://github.com/NguyenQuangViet123/NextFE) and PR
  [#17336](https://github.com/files-community/Files/pull/17336), with fixes: automatic wake-up of Everything,
  native fallback when unavailable, `$RECYCLE.BIN` result filtering, and AQS/tag/WSL/network routing.