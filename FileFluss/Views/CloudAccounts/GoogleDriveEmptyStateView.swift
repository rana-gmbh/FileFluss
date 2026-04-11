import SwiftUI

/// Shown in the right panel when the user selects a Google Drive account row in
/// the sidebar and has not yet picked any folders. Under the `drive.file` scope,
/// FileFluss cannot enumerate the user's Drive — folders must be picked
/// explicitly via Google's Picker.
struct GoogleDriveEmptyStateView: View {
    let accountId: UUID
    @Environment(AppState.self) private var appState
    @State private var isPresenting = false

    var body: some View {
        VStack(spacing: 20) {
            Image(systemName: "externaldrive.connected.to.line.below")
                .font(.system(size: 56, weight: .light))
                .foregroundStyle(.secondary)

            Text("No folders connected yet")
                .font(.title2)

            Text("FileFluss only sees the folders you pick. Choose which folders in your Google Drive to sync and browse here.")
                .multilineTextAlignment(.center)
                .foregroundStyle(.secondary)
                .frame(maxWidth: 420)
                .fixedSize(horizontal: false, vertical: true)

            Button {
                presentPicker()
            } label: {
                HStack(spacing: 6) {
                    if isPresenting {
                        ProgressView().scaleEffect(0.7)
                    } else {
                        Image(systemName: "plus.circle.fill")
                    }
                    Text("Pick folders…")
                }
                .padding(.horizontal, 8)
            }
            .controlSize(.large)
            .buttonStyle(.borderedProminent)
            .disabled(isPresenting)
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .padding(40)
    }

    private func presentPicker() {
        guard !isPresenting else { return }
        isPresenting = true
        Task {
            await appState.presentGoogleDrivePicker(for: accountId)
            isPresenting = false
        }
    }
}
