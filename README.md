git-giant
==========

Built for game developers who are used to the world of versioned game data, git-giant will transparently handle your large data separatly from the rest of your git repository. You should never need to directly invoke git-giant after your initial init/clone.

**Important!** *There is an issue with git which can wreak havok on your repo if you fail to catch and properly configure all file attributes before you commit them for the first time through a filter. See https://github.com/LuminawesomeGames/git-giant/issues/1 for more details*


## Goals ##
1. Allow users to version large binaries easily with along side source code.
2. Binary handling must be transparent to non-power users and other tools so that anyone and anything that uses git doesn't require knowlege of git-giant.
3. Allow git power users to work locally in all their normal distributed git glory.
4. Allow users on very large, multi-terabyte projects to fetch binaries exclusivly from a central store. No large file history lives on the client, only the current working set.

## Getting Started ##

### New Repository ###
#### Initing git-giant ####

git-giant uses git hooks & filters to transparently keep portions of your repo in an external store. In order to install those hooks you need to run `git giant init` in a repository.

    git init new/repo/dir
    cd new/repo/dir
    git giant init

####Filtering files with .gitattributes####
Add any files and extentions you'd like giant to track to your `.gitattributes`

    *.max filter=giant
    *.psd filter=giant
    *.wav filter=giant

####Configuring .gitgiant####
git-giant has it's own config file which you use to tell it which stores will be updated when a push is made to a particular remote. It also provides some rules for helping catch new files that may have been missed in `.gitattributes`.

    [repo]
	    text-size-threshold = 5242880
	    bin-size-threshold = 102400

    [store "project-store.onsite"]
      remote = /local/project.git
	    url = /local/project_store

    [store "my-git-store"]
	    remote = https://github.com/user/project.git
	    url = ftps://mydomain.com/project_store
        username = user
        password = pass
        primary = true

In the `[repo]` section, the `text-size-threshold` & `bin-size-threshold` limits are used to help catch new files that may be too big for a git repo. When you perform a commit, git-giant will verify that the staged files are all within appropriate limits. If they're not, it will abort the commit. You can then take action by either bumping up your limits or adding the file/filetype to `.gitattributes`.

The `[store]` sections are used to map git remote repositories to git-giant store locations. The idea here being that when pefrorm `git-push` to a particular remote, the stores that are mapped to that remote are also updated automatically.

Each `[store]` section should have a unique name `[store "store-name"]` which adheres to the git-config format.

Currently stores can be accessed from `file://`, `ftp://`, `ftps://`, `sftp://`

##### Configuring .gitgiantuser #####
User specific override file which should be included in `.gitignore`. This file is intended to specify usernames and passwords for git-giant stores.

For example `.gitgiant` may contain `[store "remote_ftp"]` with a username and password for read-only access. With `.gitgiantuser`, users may override the username and password with their own credentials which enable read/write.

    [store "my-git-store"]
        username = write_access_user
        password = pass

### Cloning Repository ###
git-giant needs to install some hooks and filters before you do your first checkout so `git giant clone` will clone the repo with --no-checkout, install the hooks and then do the checkout.

    git giant clone <repository> <directory>

### Common .gitattributes ###

	# Common
	*.bmp  filter=giant binary
	*.exe  filter=giant binary
	*.dae  filter=giant binary
	*.dll  filter=giant binary
	*.fbx  filter=giant binary
	*.ico  filter=giant binary
	*.jpg  filter=giant binary
	*.ma   filter=giant binary
	*.max  filter=giant binary
	*.mb   filter=giant binary
	*.mp3  filter=giant binary
	*.obj  filter=giant binary
	*.ogg  filter=giant binary
	*.png  filter=giant binary
	*.psd  filter=giant binary
	*.so   filter=giant binary
	*.tga  filter=giant binary
	*.ttf  filter=giant binary
	*.tiff filter=giant binary
	*.ztl  filter=giant binary
	*.wav  filter=giant binary

	# UE4
	*.uasset filter=giant binary
	*.umap   filter=giant binary

	# Unity
	*.unity filter=giant binary

### Known Issues ###

### TODO ###
- Trim internal store after a pushing to a primary store
- If you download a git archive from github, you'll only have the proxy files instead of the actual binary data. Add support for sucking down data from proxy files only without a git repo.
- If you perform `git clone` rather than a `git giant clone`, you're hooped and you'll have to start over. This kind of sucks.
- Add support for optionally compressing files before uploading to store
