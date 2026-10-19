#ifndef PCH_ENABLED
	#include <QCoreApplication>
	#include <QLocale>
	#include <QFile>
	#include <QString>
	#include <QResource>
	#include <QDir>
	#include <QStringList>

	#include <exception>
	#include <iostream>
	#include <cassert>
	#include <cstdlib>
	#include <cstdio>
#endif

#include <csignal>

#ifndef NOMINMAX
	#define NOMINMAX
#endif
#include <windows.h>
#include <process.h>
#include <io.h>
#include <fcntl.h>

#include <OverglowConfig.h>

#include "SystrayHandler.h"
#include <utils/Logger.h>
#include <commandline/Parser.h>
#include <db/AuthTable.h>

#include "detectProcess.h"
#include "AppDaemon.h"
#include "LegacyMigration.h"

using namespace commandline;

void CreateConsole()
{
	if (!AllocConsole()) {
		return;
	}

	FILE* fDummy;
	SetConsoleOutputCP(CP_UTF8);
	_setmode(_fileno(stdout), _O_U16TEXT);
	freopen_s(&fDummy, "CONOUT$", "w", stdout);
	freopen_s(&fDummy, "CONOUT$", "w", stderr);
	freopen_s(&fDummy, "CONIN$", "r", stdin);
	SetConsoleTitle(TEXT("Overglow"));

}

#define PERM0664 (QFileDevice::ReadOwner | QFileDevice::ReadGroup | QFileDevice::ReadOther | QFileDevice::WriteOwner | QFileDevice::WriteGroup)

AppDaemon* appDaemon = nullptr;

QCoreApplication* createApplication(bool& isGuiApp, int& argc, char* argv[])
{
	// the systray is shown unless the app runs as a console service
	isGuiApp = true;
	for (int i = 1; i < argc; ++i)
	{
		if (qstrcmp(argv[i], "--service") == 0)
		{
			isGuiApp = false;
		}
	}

	QCoreApplication* app = new QCoreApplication(argc, argv);
	QCoreApplication::setApplicationName("Overglow");
	QCoreApplication::setApplicationVersion(OVERGLOW_VERSION);

	// add optional library path
	QCoreApplication::addLibraryPath(QCoreApplication::applicationDirPath() + "/../lib");

	return app;
}

int main(int argc, char** argv)
{
	// check if we are running already an instance
	const char* processName = "overglow.exe";

	// Initialising QCoreApplication
	bool isGuiApp = false;
	QScopedPointer<QCoreApplication> app(createApplication(isGuiApp, argc, argv));

	// force the locale
	setlocale(LC_ALL, "C");
	QLocale::setDefault(QLocale::c());

	Parser parser("Overglow Daemon");
	parser.addHelpOption();

	const QString defaultDataPath = LegacyMigration::defaultDataPath();

	BooleanOption& versionOption = parser.add<BooleanOption>(0x0, "version", "Show version information");
	Option& userDataOption = parser.add<Option>('u', "userdata", "Overwrite user data path, defaults to home directory of current user (%1)", defaultDataPath);
	BooleanOption& resetPassword = parser.add<BooleanOption>(0x0, "resetPassword", "Lost your password? Reset it with this option back to '" + QString(overglow::DEFAULT_CONFIG_PASSWORD) + "'");
	BooleanOption& deleteDB = parser.add<BooleanOption>(0x0, "deleteDatabase", "Start all over? This Option will delete the database");
	BooleanOption& silentOption = parser.add<BooleanOption>('s', "silent", "Do not print any outputs");
	BooleanOption& verboseOption = parser.add<BooleanOption>('v', "verbose", "Increase verbosity");
	BooleanOption& debugOption = parser.add<BooleanOption>('d', "debug", "Show debug messages");
	BooleanOption& consoleOption = parser.add<BooleanOption>('c', "console", "Open a console window to view log output");
	parser.add<BooleanOption>(0x0, "desktop", "Show systray on desktop");
	parser.add<BooleanOption>(0x0, "service", "Force Overglow to start as console service");

	/* Internal options, invisible to help */
	BooleanOption& waitOption = parser.addHidden<BooleanOption>(0x0, "wait-for-others", "Do not exit if other Overglow processes are running, wait for them to finish");

	parser.process(*qApp);

	if (parser.isSet(versionOption))
	{
		std::cout
			<< "Overglow Ambient Light Daemon" << std::endl
			<< "\tVersion   : " << OVERGLOW_VERSION << " (" << OVERGLOW_BUILD_ID << ")" << std::endl
			<< "\tBuild Time: " << __DATE__ << " " << __TIME__ << std::endl;

		return 0;
	}

	if (!parser.isSet(waitOption))
	{
		if (getProcessIdsByProcessName(processName).size() > 1)
		{
			std::cerr << "The Overglow daemon is already running, abort start";
			return 1;
		}
	}
	else
	{
		while (getProcessIdsByProcessName(processName).size() > 1)
		{
			QThread::msleep(100);
		}
	}

	if (parser.isSet(consoleOption))
	{
		CreateConsole();
	}

	// initialize main logger and set global log level
	LoggerName log("MAIN");
	Logger::getInstance()->setLogLevel(Logger::INFO);

	int logLevelCheck = 0;
	if (parser.isSet(silentOption))
	{
		Logger::getInstance()->setLogLevel(Logger::OFF);
		logLevelCheck++;
	}

	if (parser.isSet(verboseOption))
	{
		Logger::getInstance()->forceVerbose();
		Logger::getInstance()->setLogLevel(Logger::INFO);
		logLevelCheck++;
	}

	if (parser.isSet(debugOption))
	{
		Logger::getInstance()->setLogLevel(Logger::DEBUG);
		logLevelCheck++;
	}

	if (logLevelCheck > 1)
	{
		Error(log, "aborting, because options --silent --verbose --debug can't be used together");
		return 0;
	}

	qInstallMessageHandler(Logger::qtMessageHandler);

	int rc = 1;
	bool readonlyMode = false;

	QString userDataPath(userDataOption.value(parser));

	// one-time migration of Overglow data: copy the legacy default directory, provide db/overglow.db
	if (!parser.isSet(userDataOption))
		LegacyMigration::migrateDefaultDataDir(userDataPath);
	LegacyMigration::migrateDatabaseFile(userDataPath);

	QDir userDataDirectory(userDataPath);

	QFileInfo dbFile(userDataDirectory.absolutePath() + "/db/" + DATABASE_FILE_NAME);

	try
	{
		if (dbFile.exists())
		{
			if (!dbFile.isReadable())
			{
				throw std::runtime_error("Configuration database '" + dbFile.absoluteFilePath().toStdString() + "' is not readable. Please setup permissions correctly!");
			}
			else
			{
				if (!dbFile.isWritable())
				{
					readonlyMode = true;
				}

				Info(log, "Database path: '{:s}', readonlyMode = {:s}", (dbFile.absoluteFilePath()), (readonlyMode) ? "enabled" : "disabled");
			}
		}
		else
		{
			if (!userDataDirectory.mkpath(dbFile.absolutePath()))
			{
				if (!userDataDirectory.isReadable() || !dbFile.isWritable())
				{
					throw std::runtime_error("The user data path '" + userDataDirectory.absolutePath().toStdString() + "' can't be created or isn't read/writeable. Please setup permissions correctly!");
				}
			}
		}

		DBManager::initializeDatabaseFilename(dbFile, readonlyMode);
		LegacyMigration::migrateDatabaseSchema();

		// reset Password without spawning daemon
		if (parser.isSet(resetPassword))
		{
			if (readonlyMode)
			{
				Error(log, "Password reset is not possible. The user data path '{:s}' is not writeable.", (userDataDirectory.absolutePath()));
				throw std::runtime_error("Password reset failed");
			}
			else
			{
				std::unique_ptr<AuthTable> table = std::make_unique<AuthTable>();
				if (table->resetDefaultUser()) {
					Info(log, "Password reset successful");
					exit(0);
				}
				else {
					Error(log, "Failed to reset password!");
					exit(1);
				}
			}
		}

		// delete database before start
		if (parser.isSet(deleteDB))
		{
			if (readonlyMode)
			{
				Error(log, "Deleting the configuration database is not possible. The user data path '{:s}' is not writeable.", (dbFile.absolutePath()));
				throw std::runtime_error("Deleting the configuration database failed");
			}
			else
			{
				if (QFile::exists(dbFile.absoluteFilePath()))
				{
					if (!QFile::remove(dbFile.absoluteFilePath()))
					{
						Info(log, "Failed to delete Database!");
						exit(1);
					}
					else
					{
						Info(log, "Configuration database deleted successfully.");
					}
				}
				else
				{
					Warning(log, "Configuration database [{:s}] does not exist!", (dbFile.absoluteFilePath()));
				}
			}
		}

		Info(log, "Starting Overglow - {:s}, {:s}, built: {:s}:{:s}", std::string_view(OVERGLOW_VERSION), std::string_view(OVERGLOW_BUILD_ID), std::string_view(__DATE__), std::string_view(__TIME__));
		Debug(log, "QtVersion [{:s}]", std::string_view(QT_VERSION_STR));

		if (!readonlyMode)
		{
			Info(log, "Set user data path to '{:s}'", (userDataDirectory.absolutePath()));
		}
		else
		{
			Warning(log, "The user data path '{:s}' is not writeable. Overglow starts in read-only mode. Configuration updates will not be persisted!", (userDataDirectory.absolutePath()));
		}

		SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);

		try
		{
			appDaemon = new AppDaemon(userDataDirectory.absolutePath(), qApp, bool(logLevelCheck));
		}
		catch (std::exception& e)
		{
			Error(log, "Main Overglow service aborted: {:s}", e.what());
			throw;
		}

		// run the application
		SystrayHandler* systray = (isGuiApp) ? new SystrayHandler(appDaemon, appDaemon->getWebPort(), userDataDirectory.absolutePath()) : nullptr;

		if (systray != nullptr && systray->isInitialized())
		{
			rc = app->exec();

			systray->close();
		}
		else
		{
			if (isGuiApp)
			{
				Error(log, "Could not initialize the systray.");
			}
			rc = app->exec();
		}

		Info(log, "The application closed with code {:d}", rc);

		delete systray;
		systray = nullptr;

		delete appDaemon;
		appDaemon = nullptr;
	}
	catch (std::exception& e)
	{
		Error(log, "Overglow aborted: {:s}", e.what());
	}

	if (parser.isSet(consoleOption))
	{
		system("pause");
	}

	return rc;
}
