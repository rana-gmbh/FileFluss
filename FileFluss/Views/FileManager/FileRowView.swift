import SwiftUI

struct FileRowView: View {
    let item: FileItem

    var body: some View {
        HStack(spacing: 8) {
            Image(systemName: item.icon)
                .foregroundStyle(item.isDirectory ? Color.accentColor : Color.secondary)
                .frame(width: 20)

            Text(item.name)
                .lineLimit(1)
                .truncationMode(.middle)
        }
    }
}
