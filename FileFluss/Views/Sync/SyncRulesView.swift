import SwiftUI

struct SyncRulesView: View {
    @Environment(AppState.self) private var appState

    var body: some View {
        VStack(spacing: 0) {
            header
            Divider()

            if appState.syncManager.syncRules.isEmpty {
                ContentUnavailableView {
                    Label("No Sync Rules", systemImage: "arrow.triangle.2.circlepath")
                } description: {
                    Text("Create a sync rule to keep your local folders in sync with the cloud.")
                } actions: {
                    Button("Add Sync Rule") {
                        appState.syncManager.isAddingRule = true
                    }
                    .buttonStyle(.borderedProminent)
                }
            } else {
                List {
                    ForEach(appState.syncManager.syncRules) { rule in
                        SyncRuleRow(rule: rule)
                    }
                    .onDelete { indexSet in
                        for index in indexSet {
                            let rule = appState.syncManager.syncRules[index]
                            appState.syncManager.removeSyncRule(rule)
                        }
                    }
                }
            }
        }
        .sheet(isPresented: Bindable(appState.syncManager).isAddingRule) {
            AddSyncRuleView()
        }
    }

    private var header: some View {
        HStack {
            Text("Sync Rules")
                .font(.title2.bold())

            Spacer()

            Button {
                Task { await appState.syncManager.syncAll() }
            } label: {
                Label("Sync All", systemImage: "arrow.triangle.2.circlepath")
            }

            Button {
                appState.syncManager.isAddingRule = true
            } label: {
                Label("Add Rule", systemImage: "plus")
            }
            .buttonStyle(.borderedProminent)
        }
        .padding()
    }
}

struct SyncRuleRow: View {
    let rule: SyncRule
    @Environment(AppState.self) private var appState

    var body: some View {
        HStack(spacing: 12) {
            Image(systemName: rule.direction.icon)
                .font(.title2)
                .foregroundStyle(statusColor)
                .frame(width: 32)

            VStack(alignment: .leading, spacing: 4) {
                Text(rule.localPath.lastPathComponent)
                    .font(.headline)

                HStack(spacing: 4) {
                    Text(rule.localPath.path())
                        .font(.caption)
                        .foregroundStyle(.secondary)
                    Image(systemName: "arrow.right")
                        .font(.caption2)
                        .foregroundStyle(.tertiary)
                    Text(rule.remotePath)
                        .font(.caption)
                        .foregroundStyle(.secondary)
                }

                if let accountName = appState.syncManager.accountFor(id: rule.accountId)?.displayName {
                    Text(accountName)
                        .font(.caption2)
                        .foregroundStyle(.tertiary)
                }
            }

            Spacer()

            VStack(alignment: .trailing, spacing: 4) {
                statusBadge

                if let lastSync = rule.lastSyncDate {
                    Text(lastSync, style: .relative)
                        .font(.caption2)
                        .foregroundStyle(.secondary)
                }
            }

            Toggle("", isOn: Binding(
                get: { rule.isEnabled },
                set: { _ in appState.syncManager.toggleRule(rule) }
            ))
            .labelsHidden()
            .toggleStyle(.switch)
            .controlSize(.small)

            Button {
                Task { await appState.syncManager.syncNow(rule: rule) }
            } label: {
                Image(systemName: "arrow.triangle.2.circlepath")
            }
            .buttonStyle(.borderless)
            .disabled(rule.status == .syncing)
        }
        .padding(.vertical, 4)
    }

    private var statusColor: Color {
        switch rule.status {
        case .idle: return .green
        case .syncing: return .blue
        case .paused: return .orange
        case .error: return .red
        }
    }

    @ViewBuilder
    private var statusBadge: some View {
        HStack(spacing: 4) {
            Circle()
                .fill(statusColor)
                .frame(width: 6, height: 6)
            Text(rule.status.displayName)
                .font(.caption)
                .foregroundStyle(.secondary)
        }
    }
}
