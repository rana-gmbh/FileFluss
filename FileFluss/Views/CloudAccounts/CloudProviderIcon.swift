import SwiftUI

/// Displays the official logo for cloud providers that have one,
/// falling back to an SF Symbol for the rest.
struct CloudProviderIcon: View {
    let providerType: CloudProviderType
    var size: CGFloat = 20

    var body: some View {
        if let asset = providerType.logoAssetName {
            Image(asset)
                .resizable()
                .aspectRatio(contentMode: .fit)
                .frame(width: size, height: size)
        } else {
            Image(systemName: providerType.icon)
                .resizable()
                .aspectRatio(contentMode: .fit)
                .frame(width: size, height: size)
        }
    }
}
