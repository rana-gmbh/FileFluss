cask "filefluss" do
  version "0.8.1"
  sha256 "29bc5a554f0ef0bb7c6236a4309d46312d6aea0b8074a4a6e6849e95743ac39e"

  url "https://github.com/rana-gmbh/filefluss/releases/download/v#{version}/FileFluss-v#{version}.dmg"
  name "FileFluss"
  desc "Dual-panel file manager with multi-cloud support"
  homepage "https://github.com/rana-gmbh/filefluss"

  depends_on macos: ">= :sonoma"

  app "FileFluss.app"

  zap trash: [
    "~/Library/Preferences/com.rana-gmbh.FileFluss.plist",
    "~/Library/Caches/com.rana-gmbh.FileFluss",
    "~/Library/Application Support/com.rana-gmbh.FileFluss",
  ]
end
