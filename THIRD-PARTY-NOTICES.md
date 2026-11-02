# Third-party components

LightSvnBrowser starts the standalone Subversion command-line client as a separate
process. The default build embeds an unmodified **Subversion 1.14.1 x64** runtime
collected from `C:\Program Files\TortoiseSVN\bin`. No TortoiseSVN GUI, Explorer
extension, updater, or crash reporter is included. `scripts/Build.ps1` can use a
different installation directory with `-SvnBinDirectory`, or omit the runtime with
`-SkipRuntime`.

The runtime ZIP is embedded in the EXE as `LightSvnBrowser.SvnRuntime.zip`. On first
use it is extracted to the selected data directory under `runtime\svn-1.14.1`.
The original license files and a generated `RUNTIME-SOURCE.txt` are extracted
beside `svn.exe`, and remain available without a network connection.

| Component | Origin and license information |
| --- | --- |
| Apache Subversion 1.14.1 | [Apache Subversion](https://subversion.apache.org/), Apache License 2.0. Preserve `Subversion license.txt`, including its third-party notices. |
| APR and APR-util | [Apache Portable Runtime](https://apr.apache.org/), Apache License 2.0 and bundled notices. See `apr License.txt`. |
| Cyrus SASL and authentication providers | [Cyrus SASL](https://www.cyrusimap.org/sasl/), Carnegie Mellon University/BSD-style license. See `CyrusSASL License.txt`. Providers are included because SASL loads them dynamically. |
| GNU gettext/libintl (`intl3_tsvn.dll`) | Internationalization library supplied by the original TortoiseSVN distribution. Retain the original distribution's corresponding source and license materials. |
| OpenSSL code included by the supplied SVN distribution | [OpenSSL](https://www.openssl.org/). Preserve the supplied `OpenSSL License.txt`; the license corresponds to the original bundled version. |
| TortoiseSVN distribution-specific components and notices | [TortoiseSVN](https://tortoisesvn.net/), GPL version 2. Preserve the original `TortoiseSVN License.txt`. Source code for the matching upstream distribution is available through the project's [source code page](https://tortoisesvn.net/sourcecode.html) and [release archive](https://sourceforge.net/projects/tortoisesvn/files/). |
| Microsoft Visual C++ runtime DLLs | App-local, unmodified Microsoft runtime DLLs supplied alongside the source SVN client, selected by native import dependencies. They are proprietary Microsoft components and are not covered by this application's license. Redistribution is subject to the applicable Microsoft Visual Studio/Visual C++ runtime terms. |

The build collects both ordinary and delay-loaded PE imports. It additionally
includes the source distribution's `sasl*.dll` authentication providers, together
with their dependency closure, to support SVN servers using SASL. Windows system
DLLs and API-set DLL contracts are supplied by the operating system; they are not
copied into the runtime. The bundled client targets **64-bit Windows 10 or later**
with .NET Framework installed for the UI. The operating system supplies Universal
CRT components.

## Authentication portability

The original Windows Cyrus SASL 2.1.24 binary uses the machine's
`HKLM\SOFTWARE\Carnegie Mellon\Project Cyrus\SASL Library\SearchPath` setting
to locate authentication providers. In this build, setting `SASL_PATH` alone does
not override that location. See the upstream [Windows SASL setup
documentation](https://tortoisesvn.net/sasl_howto.html). This application does not
change the machine-wide registry setting. Standard HTTP(S) authentication and
ordinary SVN authentication use the bundled client; servers that require
additional Cyrus SASL mechanisms need a suitable system configuration/client.
The optional GSSAPI provider additionally needs a compatible MIT Kerberos
runtime (`gssapi64.dll`), which the source TortoiseSVN installation does not
include. No unrelated software's Kerberos runtime is copied into this package.

This file records component provenance; the original upstream license texts are
authoritative. For redistribution of the assembled product, retain these notices
and the corresponding upstream license/source materials required by each
component's terms.
