#pragma once

#ifndef PCH_ENABLED
	#include <QString>
#endif

///
/// One-time migration of user data created by HyperHDR, the project this product is based on.
/// Legacy data is only ever copied, never moved, modified or deleted.
///
/// For testing, the default locations can be overridden with environment variables:
///   OVERGLOW_DATA_DIR         - the default user data directory of this product
///   OVERGLOW_LEGACY_DATA_DIR  - the legacy default user data directory to migrate from
///
namespace LegacyMigration
{
	/// Default user data directory (used when --userdata is not given). Has no side effects.
	QString defaultDataPath();

	/// If newDataPath does not exist yet but the legacy default data directory does,
	/// copy the legacy directory to newDataPath and rename its database file.
	void migrateDefaultDataDir(const QString& newDataPath);

	/// If <userDataPath>/db holds only a legacy database file, copy it to the current file name.
	void migrateDatabaseFile(const QString& userDataPath);

	/// Upgrade a legacy database schema in place (instance column, default user and password).
	/// Must run after DBManager::initializeDatabaseFilename() and before any table is used.
	void migrateDatabaseSchema();
}
