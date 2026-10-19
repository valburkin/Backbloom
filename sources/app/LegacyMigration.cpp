// One-time migration of user data created by HyperHDR, the MIT-licensed project this product is based on.

#ifndef PCH_ENABLED
	#include <QDir>
	#include <QFile>
	#include <QFileInfo>
	#include <QString>
#endif

#include <QDirIterator>
#include <QStandardPaths>

#include <utils/Logger.h>
#include <db/DBManager.h>
#include <db/AuthTable.h>
#include <db/SQLite.h>

#include "LegacyMigration.h"

namespace
{
	// Names used by HyperHDR. They only appear here, to recognize and migrate legacy data.
	constexpr const char* LEGACY_HOME_DIR_NAME = ".hyperhdr";
	constexpr const char* LEGACY_APP_DIR_NAME = "HyperHDR";
	constexpr const char* LEGACY_DATABASE_FILE_NAME = "hyperhdr.db";
	constexpr const char* LEGACY_INSTANCE_COLUMN = "hyperhdr_instance";
	constexpr const char* LEGACY_DEFAULT_USER = "Hyperhdr";
	constexpr const char* LEGACY_DEFAULT_PASSWORD = "hyperhdr";

	constexpr const char* HOME_DIR_NAME = ".overglow";
	constexpr const char* ENV_DATA_DIR = "OVERGLOW_DATA_DIR";
	constexpr const char* ENV_LEGACY_DATA_DIR = "OVERGLOW_LEGACY_DATA_DIR";

	// SQLite side files that must travel with the database file
	const QStringList DATABASE_SUFFIXES = { "", "-wal", "-journal" };

	QString envPath(const char* name)
	{
		const QString value = qEnvironmentVariable(name).trimmed();
		return value.isEmpty() ? QString() : QDir::cleanPath(QDir(value).absolutePath());
	}

	QString homePath(const char* dirName)
	{
		return QDir(QDir::homePath()).absoluteFilePath(dirName);
	}

	QString legacyDefaultDataPath()
	{
		QString overridden = envPath(ENV_LEGACY_DATA_DIR);
		if (!overridden.isEmpty())
			return overridden;

		if (QDir(homePath(LEGACY_HOME_DIR_NAME)).exists())
			return homePath(LEGACY_HOME_DIR_NAME);

		// QStandardPaths::AppConfigLocation as HyperHDR resolved it: same base folder, its application name
		QDir appConfig(QStandardPaths::writableLocation(QStandardPaths::AppConfigLocation));
		return QDir::cleanPath(appConfig.absoluteFilePath(QString("../%1").arg(LEGACY_APP_DIR_NAME)));
	}

	bool copyTree(const QString& source, const QString& destination, LoggerName& log)
	{
		const QDir sourceDir(source);
		if (!QDir().mkpath(destination))
		{
			Error(log, "Cannot create '{:s}'", destination);
			return false;
		}

		QDirIterator it(source, QDir::AllEntries | QDir::Hidden | QDir::System | QDir::NoDotAndDotDot, QDirIterator::Subdirectories);
		while (it.hasNext())
		{
			const QString path = it.next();
			const QFileInfo info(path);
			const QString target = QDir(destination).absoluteFilePath(sourceDir.relativeFilePath(path));

			if (info.isDir() && !info.isSymLink())
			{
				if (!QDir().mkpath(target))
				{
					Error(log, "Cannot create '{:s}'", target);
					return false;
				}
			}
			else if (info.isFile())
			{
				QDir().mkpath(QFileInfo(target).absolutePath());
				if (!QFile::copy(path, target))
				{
					Error(log, "Cannot copy '{:s}' to '{:s}'", path, target);
					return false;
				}
			}
		}
		return true;
	}

	// Copies (or renames, when keepLegacy is false) db/hyperhdr.db and its side files to db/overglow.db
	bool provideDatabaseFile(const QString& dataPath, bool keepLegacy, LoggerName& log)
	{
		const QDir dbDir(QDir(dataPath).absoluteFilePath("db"));
		const QString legacyDb = dbDir.absoluteFilePath(LEGACY_DATABASE_FILE_NAME);
		const QString currentDb = dbDir.absoluteFilePath(DATABASE_FILE_NAME);

		if (QFileInfo::exists(currentDb) || !QFileInfo::exists(legacyDb))
			return true;

		for (const QString& suffix : DATABASE_SUFFIXES)
		{
			const QString from = legacyDb + suffix;
			const QString to = currentDb + suffix;
			if (!QFileInfo::exists(from))
				continue;

			const bool ok = keepLegacy ? QFile::copy(from, to) : QFile::rename(from, to);
			if (!ok)
			{
				Error(log, "Cannot {:s} '{:s}' to '{:s}'", (keepLegacy ? "copy" : "rename"), from, to);
				if (keepLegacy)
					for (const QString& partial : DATABASE_SUFFIXES)
						QFile::remove(currentDb + partial);
				return false;
			}
		}

		Info(log, "{:s} the legacy HyperHDR database '{:s}' to '{:s}'", (keepLegacy ? "Copied" : "Renamed"), legacyDb, currentDb);
		return true;
	}

	class LegacySchema : public DBManager
	{
	public:
		void upgrade(LoggerName& log)
		{
			SqlDatabase* db = getDB();

			if (db->doesTableExist("settings") && db->doesColumnExist("settings", LEGACY_INSTANCE_COLUMN) &&
				!db->doesColumnExist("settings", SETTINGS_INSTANCE_COLUMN))
			{
				SqlQuery query(db);
				if (query.exec(QString("ALTER TABLE settings RENAME COLUMN %1 TO %2;").arg(LEGACY_INSTANCE_COLUMN).arg(SETTINGS_INSTANCE_COLUMN)))
					Info(log, "Upgraded the legacy settings table (column '{:s}' renamed to '{:s}')", LEGACY_INSTANCE_COLUMN, SETTINGS_INSTANCE_COLUMN);
				else
					Error(log, "Failed to upgrade the legacy settings table: {:s}", db->error());
			}

			if (db->doesTableExist("auth"))
			{
				setTable("auth");
				const VectorPair legacyUser{ CPair("user", LEGACY_DEFAULT_USER) };
				const VectorPair currentUser{ CPair("user", overglow::DEFAULT_CONFIG_USER) };
				if (recordExists(legacyUser) && !recordExists(currentUser))
				{
					QVariantMap columns;
					columns["user"] = overglow::DEFAULT_CONFIG_USER;
					if (updateRecord(legacyUser, columns))
						Info(log, "Renamed the legacy default user '{:s}' to '{:s}'", LEGACY_DEFAULT_USER, overglow::DEFAULT_CONFIG_USER);
				}
			}
		}
	};
}

QString LegacyMigration::defaultDataPath()
{
	QString overridden = envPath(ENV_DATA_DIR);
	if (!overridden.isEmpty())
		return overridden;

	// users who kept their data in the home folder get the same layout under the new name
	if (QDir(homePath(HOME_DIR_NAME)).exists() || QDir(homePath(LEGACY_HOME_DIR_NAME)).exists())
		return homePath(HOME_DIR_NAME);

	return QStandardPaths::writableLocation(QStandardPaths::AppConfigLocation);
}

void LegacyMigration::migrateDefaultDataDir(const QString& newDataPath)
{
	LoggerName log("MIGRATION");

	const QString target = QDir::cleanPath(QDir(newDataPath).absolutePath());
	if (QFileInfo::exists(target))
		return;

	const QString legacy = legacyDefaultDataPath();
	if (legacy.isEmpty() || QDir::cleanPath(legacy) == target || !QDir(legacy).exists())
		return;

	Info(log, "First start: copying the legacy HyperHDR data directory '{:s}' to '{:s}' (the legacy directory is left untouched)", legacy, target);

	const QString staging = target + ".migrating";
	if (QFileInfo::exists(staging))
		QDir(staging).removeRecursively();

	if (!copyTree(legacy, staging, log) || !provideDatabaseFile(staging, false, log) || !QDir().rename(staging, target))
	{
		Error(log, "Migration of the legacy data directory failed. Starting with a fresh configuration.");
		QDir(staging).removeRecursively();
		return;
	}

	Info(log, "The legacy HyperHDR data directory has been migrated to '{:s}'", target);
}

void LegacyMigration::migrateDatabaseFile(const QString& userDataPath)
{
	LoggerName log("MIGRATION");
	provideDatabaseFile(userDataPath, true, log);
}

void LegacyMigration::migrateDatabaseSchema()
{
	LoggerName log("MIGRATION");
	try
	{
		LegacySchema schema;
		schema.upgrade(log);

		AuthTable auth;
		if (auth.userExist(overglow::DEFAULT_CONFIG_USER) &&
			auth.calcPasswordHashOfUser(overglow::DEFAULT_CONFIG_USER, LEGACY_DEFAULT_PASSWORD) == auth.getPasswordHashOfUser(overglow::DEFAULT_CONFIG_USER) &&
			auth.updateUserPassword(overglow::DEFAULT_CONFIG_USER, overglow::DEFAULT_CONFIG_PASSWORD))
		{
			Info(log, "The legacy HyperHDR default password was still in use. It has been replaced by the default password of this product.");
		}
	}
	catch (std::exception& e)
	{
		Error(log, "Legacy database upgrade failed: {:s}", e.what());
	}
}
