if(USE_PRECOMPILED_HEADERS AND COMMAND target_precompile_headers)
	file(WRITE ${CMAKE_CURRENT_BINARY_DIR}/precompiled_headers.cpp "void precompiledHeadersStub(){};")
	add_library(precompiled_headers ${CMAKE_CURRENT_BINARY_DIR}/precompiled_headers.cpp)
	target_link_libraries(precompiled_headers PRIVATE Qt6::Network Qt6::Core)
	target_precompile_headers(precompiled_headers PRIVATE ${CMAKE_SOURCE_DIR}/include/precompiled_headers/AllHeaders_pch.h )
endif()

# Define the current source locations
SET(CURRENT_HEADER_DIR ${CMAKE_SOURCE_DIR}/include)
SET(CURRENT_SOURCE_DIR ${CMAKE_SOURCE_DIR}/sources)

add_subdirectory(api)
add_subdirectory(base)
add_subdirectory(blackborder)
add_subdirectory(bonjour)
add_subdirectory(commandline)
add_subdirectory(db)
add_subdirectory(effects)
add_subdirectory(grabber)
add_subdirectory(hyperimage)
add_subdirectory(image)
add_subdirectory(json-utils)
add_subdirectory(led-drivers)
add_subdirectory(mqtt)
add_subdirectory(performance-counters)
add_subdirectory(infinite-color-engine)
add_subdirectory(sound-capture)
add_subdirectory(ssdp)
add_subdirectory(suspend-handler)
add_subdirectory(systray)
add_subdirectory(utils)
add_subdirectory(utils-image)
add_subdirectory(utils-zstd)
add_subdirectory(webserver)

# Executables
add_subdirectory(app)


