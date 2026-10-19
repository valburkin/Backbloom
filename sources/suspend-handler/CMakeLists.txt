# Define the current source locations
SET(CURRENT_HEADER_DIR ${CMAKE_SOURCE_DIR}/include/suspend-handler)
SET(CURRENT_SOURCE_DIR ${CMAKE_SOURCE_DIR}/sources/suspend-handler)

add_library(suspend-handler OBJECT
	${CURRENT_HEADER_DIR}/SuspendHandlerWindows.h
	${CURRENT_SOURCE_DIR}/SuspendHandlerWindows.cpp
)
target_link_libraries(suspend-handler PUBLIC Qt6::Core Qt6::Network)

if(USE_PRECOMPILED_HEADERS AND COMMAND target_precompile_headers)
	target_precompile_headers(suspend-handler REUSE_FROM precompiled_headers)
endif()
