import SwiftUI

struct FileRowView: View {
    let item: FileItem
    let isDropTarget: Bool

    init(item: FileItem, isDropTarget: Bool = false) {
        self.item = item
        self.isDropTarget = isDropTarget
    }

    var body: some View {
        HStack(spacing: 0) {
            // Name column
            HStack(spacing: 8) {
                Image(systemName: item.icon)
                    .foregroundStyle(item.isDirectory ? Color.accentColor : Color.secondary)
                    .frame(width: 20)

                Text(item.name)
                    .lineLimit(1)
                    .truncationMode(.middle)
            }
            .frame(maxWidth: .infinity, alignment: .leading)

            // Date column
            Text(item.formattedDate)
                .foregroundStyle(.secondary)
                .frame(width: 160, alignment: .leading)

            // Size column
            Text(item.formattedSize)
                .foregroundStyle(.secondary)
                .monospacedDigit()
                .frame(width: 80, alignment: .trailing)
        }
        .padding(.vertical, 2)
        .background(
            isDropTarget
                ? RoundedRectangle(cornerRadius: 4).fill(Color.accentColor.opacity(0.2))
                : nil
        )
    }
}
