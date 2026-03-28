import SwiftUI

struct AddCloudAccountView: View {
    @Environment(AppState.self) private var appState
    @Environment(\.dismiss) private var dismiss

    var body: some View {
        VStack(spacing: 20) {
            Text("Add Cloud Account")
                .font(.title2.bold())

            Text("Select a cloud provider to connect:")
                .foregroundStyle(.secondary)

            LazyVGrid(columns: [
                GridItem(.flexible()),
                GridItem(.flexible()),
            ], spacing: 16) {
                ForEach(CloudProviderType.allCases) { provider in
                    Button {
                        Task {
                            await appState.syncManager.addAccount(type: provider)
                            dismiss()
                        }
                    } label: {
                        VStack(spacing: 8) {
                            Image(systemName: provider.icon)
                                .font(.largeTitle)
                                .frame(height: 40)
                            Text(provider.displayName)
                                .font(.headline)
                        }
                        .frame(maxWidth: .infinity)
                        .padding()
                        .background(.quaternary)
                        .clipShape(RoundedRectangle(cornerRadius: 12))
                    }
                    .buttonStyle(.plain)
                }
            }

            Button("Cancel") { dismiss() }
                .keyboardShortcut(.cancelAction)
        }
        .padding(24)
        .frame(width: 400)
    }
}
