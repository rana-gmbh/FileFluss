# FileFluss 0.6.0 Beta

## Automatic Google Workspace Document Conversion

When copying or moving files from Google Drive to local folders or other cloud services, Google Workspace documents are now **automatically converted into formats editable by LibreOffice and Microsoft Office**:

| Google Workspace Format | Converted To |
|---|---|
| Google Docs | **DOCX** (Word) |
| Google Sheets | **XLSX** (Excel) |
| Google Slides | **PPTX** (PowerPoint) |
| Google Drawings | PDF |

- Conversion happens server-side via Google's export API — fast and lossless
- Exported files are saved with the correct file extension (e.g. `My Document.docx`)
- Works for both cloud-to-local and cloud-to-cloud transfers (e.g. Google Drive → pCloud)
- Quick Look previews also use the converted format

## Bug Fixes

- Fixed cloud-to-cloud transfers silently failing for Google Workspace files (download succeeded but upload found no file due to filename mismatch)
- Fixed overwrite detection for converted Google Workspace files
- Fixed Quick Look temp cache not finding previously converted files

## Full Changelog

See the [commit history](https://github.com/rana-gmbh/filefluss/commits/main) for the complete list of changes.
